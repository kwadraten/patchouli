using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Patchouli.Core.Diagnostics;

/// <summary>
/// Records blocking-resource entry on the UI thread without throwing or changing business results.
/// Hosts without a UI never install a probe. Keys contain methods, never resource paths or content.
/// </summary>
public static class UiThreadExecutionGuard
{
    public const string Sqlite = "SQLite";
    public const string Ocr = "OCR";
    private static Func<bool>? _isUiThread;
    private static readonly ConcurrentDictionary<string, long> Counts = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, UiThreadExecutionViolation> Violations =
        new(StringComparer.Ordinal);

    public static Func<bool>? Configure(Func<bool>? isUiThread)
    {
        return Interlocked.Exchange(ref _isUiThread, isUiThread);
    }

    public static long GetCount(string resource)
    {
        return Counts.GetValueOrDefault(resource);
    }

    public static IReadOnlyList<UiThreadExecutionViolation> GetViolations()
    {
        return Violations.Values.OrderBy(violation => violation.Key, StringComparer.Ordinal).ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Check(string resource)
    {
        Func<bool>? probe = Volatile.Read(ref _isUiThread);
        if (probe is null)
        {
            return;
        }

        try
        {
            if (!probe())
            {
                return;
            }

            Counts.AddOrUpdate(resource, 1, (_, count) => count + 1);
            StackTrace stack = new(false);
            string? uiMethod = FindUiMethod(stack);
            string key = resource + ":" + (uiMethod ?? "<external>");
            if (Violations.ContainsKey(key))
            {
                return;
            }

            UiThreadExecutionViolation violation = new(key, resource, uiMethod, stack.ToString());
            if (Violations.TryAdd(key, violation))
            {
                UnexpectedExceptionReporter.Report(new InvalidOperationException(
                        $"Blocking resource {resource} executed on the UI thread at {uiMethod ?? "<external>"}.\n{violation.Stack}"),
                    "ui-thread-execution", key);
            }
        }
        catch (Exception exception)
        {
            // A broken/retired dispatcher or diagnostic sink must never replace a business result.
            Debug.WriteLine($"UI execution diagnostic failed: {exception}");
        }
    }

    private static string? FindUiMethod(StackTrace stack)
    {
        foreach (StackFrame frame in stack.GetFrames())
        {
            MethodBase? method = frame.GetMethod();
            string? assembly = method?.Module.Assembly.GetName().Name;
            if (assembly is "Patchouli.Tests" or "Patchouli.Performance")
            {
                // Direct fixture preparation has no production UI caller. A continuation below
                // the test frame must not be mistaken for the owner of that service call.
                return null;
            }

            if (assembly == "Patchouli.UI")
            {
                return DescribeUiMethod(method!);
            }
        }

        return null;
    }

    private static string DescribeUiMethod(MethodBase method)
    {
        Type type = method.DeclaringType!;
        string name = method.Name;
        if (name == "MoveNext")
        {
            name = type.Name;
        }

        int start = name.LastIndexOf('<');
        int end = name.IndexOf('>', Math.Max(0, start));
        if (start >= 0 && end > start)
        {
            name = name[(start + 1)..end];
        }

        while (type.DeclaringType is { } parent && type.Name.StartsWith('<'))
        {
            type = parent;
        }

        return type.FullName + "." + name;
    }
}

public sealed record UiThreadExecutionViolation(string Key, string Resource, string? UiMethod, string Stack);
