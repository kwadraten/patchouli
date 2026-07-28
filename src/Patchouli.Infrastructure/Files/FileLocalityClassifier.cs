using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Patchouli.Core.Files;

namespace Patchouli.Infrastructure.Files;

/// <summary>
/// Classifies whether a file is local-ready, cloud-hydrated, or a cloud placeholder
/// without reading file contents (no full hash / PDF open).
/// <para>
/// Windows uses the three OS mechanisms documented for cloud/on-demand files:
/// 1) File attributes (OFFLINE / PINNED / UNPINNED / RECALL_ON_*),
/// 2) Reparse points with IO_REPARSE_TAG_CLOUD* (CldFlt placeholders),
/// 3) Cloud Files API placeholder state (cfapi / cldapi).
/// Path-name heuristics are intentionally not used.
/// </para>
/// </summary>
public static class FileLocalityClassifier
{
    public static FileLocalityAssessment Assess(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new FileLocalityAssessment(
                FileLocalityReadiness.CloudUnready,
                false,
                FileLocalityCodes.CloudNotDownloaded,
                "Path is empty.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return new FileLocalityAssessment(
                FileLocalityReadiness.CloudUnready,
                false,
                FileLocalityCodes.CloudNotDownloaded,
                "Path could not be normalized.");
        }

        if (OperatingSystem.IsWindows())
        {
            return AssessWindows(fullPath);
        }

        // macOS iCloud is handled by MaterializeFileAsync before candidates are built.
        return new FileLocalityAssessment(FileLocalityReadiness.LocalReady, false);
    }

    public static int ImportPriority(string readiness)
    {
        return readiness switch
        {
            FileLocalityReadiness.LocalReady => 0,
            FileLocalityReadiness.CloudReady => 1,
            FileLocalityReadiness.CloudUnready => 2,
            _ => 3
        };
    }

    public static IOrderedEnumerable<T> OrderForImport<T>(
        IEnumerable<T> candidates,
        Func<T, string> readinessSelector,
        Func<T, string> fileNameSelector)
    {
        return candidates
            .OrderBy(item => ImportPriority(readinessSelector(item)))
            .ThenBy(item => fileNameSelector(item), StringComparer.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static FileLocalityAssessment AssessWindows(string fullPath)
    {
        // FindFirstFile exposes enumeration-only bits such as RECALL_ON_OPEN and the
        // reparse tag in dwReserved0. GetFileAttributes fills gaps some providers report
        // only through the non-enumeration path. Merge cloud-relevant bits from both.
        if (!NativeMethods.TryFindFirst(fullPath, out NativeMethods.Win32FindData findData))
        {
            return new FileLocalityAssessment(
                FileLocalityReadiness.CloudUnready,
                false,
                FileLocalityCodes.CloudNotDownloaded,
                "File attributes are unavailable.");
        }

        uint findAttributes = findData.FileAttributes;
        uint getAttributes = NativeMethods.GetFileAttributesW(fullPath);
        uint attributes = findAttributes;
        if (getAttributes != NativeMethods.InvalidFileAttributes)
        {
            attributes |= getAttributes & NativeMethods.CloudAttributeMask;
        }

        uint reparseTag = Has(findAttributes, NativeMethods.FileAttributeReparsePoint)
            ? findData.Reserved0
            : 0u;

        // 1) File attributes (attrib O/P/U and recall bits)
        bool offline = Has(attributes, NativeMethods.FileAttributeOffline);
        bool pinned = Has(attributes, NativeMethods.FileAttributePinned);
        bool unpinned = Has(attributes, NativeMethods.FileAttributeUnpinned);
        bool recallOnDataAccess = Has(attributes, NativeMethods.FileAttributeRecallOnDataAccess);
        bool recallOnOpen = Has(attributes, NativeMethods.FileAttributeRecallOnOpen);

        // 2) Reparse point + IO_REPARSE_TAG_CLOUD* (CldFlt placeholder)
        bool cloudReparse = Has(attributes, NativeMethods.FileAttributeReparsePoint) &&
                            IsCloudReparseTag(reparseTag);

        // 3) Cloud Files API placeholder state
        int placeholderState = NativeMethods.TryGetPlaceholderStateFromFindData(ref findData);
        if (placeholderState == NativeMethods.CfPlaceholderStateInvalid ||
            placeholderState == NativeMethods.CfPlaceholderStateNoStates)
        {
            placeholderState = NativeMethods.TryGetPlaceholderStateFromAttributeTag(attributes, reparseTag);
        }

        bool cfPlaceholder = HasState(placeholderState, NativeMethods.CfPlaceholderStatePlaceholder);
        bool cfSyncRoot = HasState(placeholderState, NativeMethods.CfPlaceholderStateSyncRoot);
        bool cfInSync = HasState(placeholderState, NativeMethods.CfPlaceholderStateInSync);
        bool cfPartial =
            HasState(placeholderState, NativeMethods.CfPlaceholderStatePartial) ||
            HasState(placeholderState, NativeMethods.CfPlaceholderStatePartiallyOnDisk);

        // Content not fully local — must not open/hash (would block on CldFlt hydration).
        bool contentNotLocal =
            offline ||
            recallOnDataAccess ||
            recallOnOpen ||
            cfPartial;

        if (contentNotLocal)
        {
            return new FileLocalityAssessment(
                FileLocalityReadiness.CloudUnready,
                true,
                FileLocalityCodes.CloudNotDownloaded,
                DescribeUnready(offline, recallOnDataAccess, recallOnOpen, cfPartial, unpinned, pinned));
        }

        // Cloud-managed but data appears fully present locally (pinned "always keep on this
        // device", hydrated placeholder, or cloud reparse without recall/offline).
        // UNPINNED alone does not mean missing content — only "may dehydrate later".
        bool cloudManaged =
            pinned ||
            unpinned ||
            cloudReparse ||
            cfPlaceholder ||
            cfSyncRoot ||
            (cfInSync && cfPlaceholder);

        if (cloudManaged)
        {
            return new FileLocalityAssessment(
                FileLocalityReadiness.CloudReady,
                true,
                null,
                DescribeCloudReady(pinned, unpinned, cloudReparse, cfPlaceholder, cfInSync));
        }

        return new FileLocalityAssessment(FileLocalityReadiness.LocalReady, false);
    }

    private static string DescribeUnready(
        bool offline,
        bool recallOnDataAccess,
        bool recallOnOpen,
        bool cfPartial,
        bool unpinned,
        bool pinned)
    {
        List<string> bits = new();
        if (offline)
        {
            bits.Add("OFFLINE");
        }

        if (recallOnDataAccess)
        {
            bits.Add("RECALL_ON_DATA_ACCESS");
        }

        if (recallOnOpen)
        {
            bits.Add("RECALL_ON_OPEN");
        }

        if (cfPartial)
        {
            bits.Add("CF_PARTIAL");
        }

        if (unpinned)
        {
            bits.Add("UNPINNED");
        }

        if (pinned)
        {
            bits.Add("PINNED");
        }

        return "Cloud content is not fully local (" + string.Join(", ", bits) +
               "); defer import until hydrated.";
    }

    private static string DescribeCloudReady(
        bool pinned,
        bool unpinned,
        bool cloudReparse,
        bool cfPlaceholder,
        bool cfInSync)
    {
        List<string> bits = new();
        if (pinned)
        {
            bits.Add("PINNED");
        }

        if (unpinned)
        {
            bits.Add("UNPINNED");
        }

        if (cloudReparse)
        {
            bits.Add("CLOUD_REPARSE");
        }

        if (cfPlaceholder)
        {
            bits.Add("CF_PLACEHOLDER");
        }

        if (cfInSync)
        {
            bits.Add("CF_IN_SYNC");
        }

        return "Cloud-provider file with local data present (" + string.Join(", ", bits) + ").";
    }

    private static bool Has(uint attributes, uint flag)
    {
        return (attributes & flag) != 0;
    }

    private static bool HasState(int state, int flag)
    {
        return state != NativeMethods.CfPlaceholderStateInvalid && (state & flag) != 0;
    }

    /// <summary>
    /// IO_REPARSE_TAG_CLOUD (0x9000001A) and CLOUD_1..CLOUD_F variants used by CldFlt /
    /// OneDrive-compatible sync engines.
    /// </summary>
    private static bool IsCloudReparseTag(uint tag)
    {
        if (tag == 0)
        {
            return false;
        }

        // IO_REPARSE_TAG_CLOUD and CLOUD_1..CLOUD_F (0x9000001A .. 0x9000F01A pattern)
        if ((tag & 0xFFFF0FFF) == 0x9000001A)
        {
            return true;
        }

        // Name-surrogate cloud tags that only preserve the low word.
        if ((tag & 0x0000FFFF) == 0x001A && (tag & 0x80000000) != 0)
        {
            return true;
        }

        return false;
    }

    private static class NativeMethods
    {
        public const uint InvalidFileAttributes = 0xFFFFFFFF;

        // File attribute constants (WinNT.h) used for on-demand / HSM / cloud files.
        public const uint FileAttributeOffline = 0x00001000; // O — content not local
        public const uint FileAttributeReparsePoint = 0x00000400;
        public const uint FileAttributeRecallOnOpen = 0x00040000; // enumeration-only virtual item
        public const uint FileAttributePinned = 0x00080000; // P — always keep on this device
        public const uint FileAttributeUnpinned = 0x00100000; // U — free up space / online-only intent
        public const uint FileAttributeRecallOnDataAccess = 0x00400000; // must hydrate on read

        /// <summary>Bits merged from GetFileAttributes into FindFirstFile results.</summary>
        public const uint CloudAttributeMask =
            FileAttributeOffline |
            FileAttributeReparsePoint |
            FileAttributeRecallOnOpen |
            FileAttributePinned |
            FileAttributeUnpinned |
            FileAttributeRecallOnDataAccess;

        // CF_PLACEHOLDER_STATE_* (cfapi.h)
        public const int CfPlaceholderStateNoStates = 0x00000000;
        public const int CfPlaceholderStatePlaceholder = 0x00000001;
        public const int CfPlaceholderStateSyncRoot = 0x00000002;
        public const int CfPlaceholderStateInSync = 0x00000008;
        public const int CfPlaceholderStatePartial = 0x00000010;
        public const int CfPlaceholderStatePartiallyOnDisk = 0x00000020;
        public const int CfPlaceholderStateInvalid = unchecked((int)0xFFFFFFFF);

        private static readonly IntPtr InvalidHandleValue = new(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct Win32FindData
        {
            public uint FileAttributes;
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint Reserved0;
            public uint Reserved1;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string FileName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string AlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string lpFileName, out Win32FindData lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr hFindFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFileAttributesW(string lpFileName);

        [DllImport("cldapi.dll", CharSet = CharSet.Unicode, EntryPoint = "CfGetPlaceholderStateFromAttributeTag")]
        private static extern int CfGetPlaceholderStateFromAttributeTag(uint fileAttributes, uint reparseTag);

        [DllImport("cldapi.dll", CharSet = CharSet.Unicode, EntryPoint = "CfGetPlaceholderStateFromFindData")]
        private static extern int CfGetPlaceholderStateFromFindData(ref Win32FindData findData);

        public static bool TryFindFirst(string path, out Win32FindData data)
        {
            IntPtr handle = FindFirstFileW(path, out data);
            if (handle == InvalidHandleValue || handle == IntPtr.Zero)
            {
                data = default;
                return false;
            }

            FindClose(handle);
            return data.FileAttributes != InvalidFileAttributes;
        }

        public static int TryGetPlaceholderStateFromAttributeTag(uint attributes, uint reparseTag)
        {
            try
            {
                return CfGetPlaceholderStateFromAttributeTag(attributes, reparseTag);
            }
            catch (DllNotFoundException)
            {
                return CfPlaceholderStateNoStates;
            }
            catch (EntryPointNotFoundException)
            {
                return CfPlaceholderStateNoStates;
            }
        }

        public static int TryGetPlaceholderStateFromFindData(ref Win32FindData findData)
        {
            try
            {
                return CfGetPlaceholderStateFromFindData(ref findData);
            }
            catch (DllNotFoundException)
            {
                return CfPlaceholderStateNoStates;
            }
            catch (EntryPointNotFoundException)
            {
                return CfPlaceholderStateNoStates;
            }
        }
    }
}
