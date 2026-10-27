namespace Patchouli.Extensions.Contracts;

using Core.Ids;
using Core.Layout;
using Core.Bibliography;
using Core.Bibliography.MetadataLookup;
using Core.Results;
using Core.Documents;
using Core.Search;

public enum ExtensionDiagnosticSeverity
{
    Error,
    Warning,
    Information
}

public sealed record ExtensionSourceLocation(string FileName, int Line, int Column, int EndLine, int EndColumn);

public sealed record ExtensionDiagnostic(
    ExtensionDiagnosticSeverity Severity,
    string Code,
    string Message,
    ExtensionSourceLocation Location);

public enum ConfigValueKind
{
    Text,
    MultilineText,
    Integer,
    Decimal,
    Boolean,
    Choice,
    Language,
    Model,
    Documents,
    PageRange,
    CredentialReference,
    FilePath,
    DirectoryPath
}

public sealed record ConfigFieldDescriptor(
    string Key,
    string Label,
    ConfigValueKind Kind,
    bool Required,
    string Description,
    string? DefaultValue = null,
    decimal? Minimum = null,
    decimal? Maximum = null,
    IReadOnlyList<string>? Choices = null)
{
    public bool FollowDefault { get; init; }
    public string? DefaultDomain { get; init; }
    public bool Hidden { get; init; }

    /// <summary>
    /// FSX-declared host-managed files backing this path field (<see cref="ConfigValueKind.FilePath"/>
    /// or <see cref="ConfigValueKind.DirectoryPath"/>). Statically validated declaration metadata;
    /// populated only when the property carries <c>ManagedFile</c> attributes.
    /// </summary>
    public IReadOnlyList<ExtensionManagedFile> ManagedFiles { get; init; } = [];
}

public static class ExtensionHostApi
{
    public const string Current = "patchouli.extensions/3";
}

public enum ExtensionKind
{
    Translation,
    Metadata,
    Ocr,
    Retrieval,
    PdfTextLayer
}

public sealed record ExtensionDescriptor(
    string Id,
    string Name,
    string Version,
    string Description,
    ExtensionKind Kind = ExtensionKind.Translation)
{
    public string HostApi { get; init; } = ExtensionHostApi.Current;
}

/// <summary>An immutable, content-addressed package view. Source keys are normalized relative paths.</summary>
public sealed class ExtensionPackageSnapshot
{
    private static readonly StringComparer SourcePathComparer = StringComparer.OrdinalIgnoreCase;

    public ExtensionPackageSnapshot(string id, IReadOnlyDictionary<string, string> sources)
        : this(id, sources, new Dictionary<string, ReadOnlyMemory<byte>>())
    {
    }

    public ExtensionPackageSnapshot(string id, IReadOnlyDictionary<string, string> sources,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> binaryFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(binaryFiles);
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Extension id must be a stable alphanumeric identifier.", nameof(id));
        }

        Dictionary<string, string> normalized = new(SourcePathComparer);
        foreach ((string path, string content) in sources)
        {
            string canonicalPath = NormalizeSourcePath(path);
            if (!IsTextSourcePath(canonicalPath))
            {
                throw new ArgumentException(
                    $"Extension sources must use a known textual format so the file store can persist them verbatim: {canonicalPath}",
                    nameof(sources));
            }

            ArgumentNullException.ThrowIfNull(content);
            if (!normalized.TryAdd(canonicalPath, content))
            {
                throw new ArgumentException($"Duplicate extension source path after normalization: {canonicalPath}",
                    nameof(sources));
            }
        }

        Dictionary<string, ReadOnlyMemory<byte>> normalizedBinaryFiles = new(SourcePathComparer);
        foreach ((string path, ReadOnlyMemory<byte> content) in binaryFiles)
        {
            string canonicalPath = NormalizeSourcePath(path);
            if (IsTextSourcePath(canonicalPath))
            {
                throw new ArgumentException(
                    $"Binary package files cannot claim a textual path; the file store partitions by format so '{canonicalPath}' must be a source: {canonicalPath}",
                    nameof(binaryFiles));
            }

            if (normalized.ContainsKey(canonicalPath))
            {
                throw new ArgumentException(
                    $"Package path is declared as both source and binary content: {canonicalPath}",
                    nameof(binaryFiles));
            }

            byte[] copy = content.ToArray();
            if (!normalizedBinaryFiles.TryAdd(canonicalPath, new ReadOnlyMemory<byte>(copy)))
            {
                throw new ArgumentException($"Duplicate extension binary path after normalization: {canonicalPath}",
                    nameof(binaryFiles));
            }
        }

        Id = id;
        Sources = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(normalized, StringComparer.Ordinal));
        BinaryFiles = new System.Collections.ObjectModel.ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(
            new Dictionary<string, ReadOnlyMemory<byte>>(normalizedBinaryFiles, StringComparer.Ordinal));
        Fingerprint = PackageFingerprint.Compute(Sources, BinaryFiles);
    }

    public static string NormalizeSourcePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains(':') ||
            normalized.IndexOfAny(['<', '>', '"', '|', '?', '*', '\0']) >= 0)
        {
            throw new ArgumentException("Extension source paths must be relative portable paths.", nameof(path));
        }

        string[] segments = normalized.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".." ||
                                           segment.EndsWith(' ') || segment.EndsWith('.')))
        {
            throw new ArgumentException("Extension source paths cannot contain empty, dot, or parent segments.",
                nameof(path));
        }

        return string.Join('/', segments);
    }

    /// <summary>Only known textual formats are decoded; all other package assets remain byte-for-byte binary.</summary>
    public static bool IsTextSourcePath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is
            ".fsx" or ".fs" or ".fsi" or ".json" or ".xml" or ".txt" or ".md" or
            ".yaml" or ".yml" or ".csv" or ".tsv" or ".toml" or ".ini" or ".config" or
            ".props" or ".targets" or ".html" or ".htm" or ".css" or ".js";
    }

    public string Id { get; }
    public IReadOnlyDictionary<string, string> Sources { get; }
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> BinaryFiles { get; }
    public string Fingerprint { get; }
}

public static class PackageFingerprint
{
    public static string Compute(IReadOnlyDictionary<string, string> sources)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, true);
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, string content) in sources.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            string canonicalPath = ExtensionPackageSnapshot.NormalizeSourcePath(path);
            if (!paths.Add(canonicalPath))
            {
                throw new ArgumentException($"Duplicate extension source path after normalization: {canonicalPath}",
                    nameof(sources));
            }

            byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(canonicalPath);
            byte[] contentBytes = System.Text.Encoding.UTF8.GetBytes(content);
            writer.Write(pathBytes.Length);
            writer.Write(pathBytes);
            writer.Write(contentBytes.Length);
            writer.Write(contentBytes);
        }

        writer.Flush();
        return Convert.ToHexString(Blake3.Hasher.Hash(stream.ToArray()).AsSpan().ToArray()).ToLowerInvariant();
    }

    public static string Compute(IReadOnlyDictionary<string, string> sources,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> binaryFiles)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(binaryFiles);
        if (binaryFiles.Count == 0)
        {
            return Compute(sources);
        }

        Dictionary<string, (bool IsBinary, byte[] Content)> entries = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, string content) in sources)
        {
            string canonicalPath = ExtensionPackageSnapshot.NormalizeSourcePath(path);
            ArgumentNullException.ThrowIfNull(content);
            if (!entries.TryAdd(canonicalPath, (false, System.Text.Encoding.UTF8.GetBytes(content))))
            {
                throw new ArgumentException($"Duplicate package path after normalization: {canonicalPath}",
                    nameof(sources));
            }
        }

        foreach ((string path, ReadOnlyMemory<byte> content) in binaryFiles)
        {
            string canonicalPath = ExtensionPackageSnapshot.NormalizeSourcePath(path);
            if (!entries.TryAdd(canonicalPath, (true, content.ToArray())))
            {
                throw new ArgumentException($"Duplicate package path after normalization: {canonicalPath}",
                    nameof(binaryFiles));
            }
        }

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, true);
        foreach ((string path, (bool isBinary, byte[] content)) in entries.OrderBy(static item => item.Key,
                     StringComparer.Ordinal))
        {
            byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(path);
            // Negative path lengths tag binary entries while leaving the legacy source-only encoding untouched.
            writer.Write(isBinary ? -pathBytes.Length : pathBytes.Length);
            writer.Write(pathBytes);
            writer.Write(content.Length);
            writer.Write(content);
        }

        writer.Flush();
        return Convert.ToHexString(Blake3.Hasher.Hash(stream.ToArray()).AsSpan().ToArray()).ToLowerInvariant();
    }
}

public sealed record ExtensionCheckResult(
    bool Succeeded,
    IReadOnlyList<ConfigFieldDescriptor> ConfigFields,
    IReadOnlyList<ExtensionDiagnostic> Diagnostics);

public sealed record ExtensionConfigSnapshot
{
    public ExtensionConfigSnapshot(IReadOnlyDictionary<string, string> values, long revision)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(values, StringComparer.Ordinal));
        Revision = revision;
    }

    public IReadOnlyDictionary<string, string> Values { get; }
    public long Revision { get; }

    public void Deconstruct(out IReadOnlyDictionary<string, string> values, out long revision)
    {
        values = Values;
        revision = Revision;
    }
}

public sealed record ExtensionModelSelection(string ProviderId, string Model);

public sealed record ExtensionPageIdentity(
    DocumentInstanceId DocumentId,
    PageId PageId,
    DocumentTreeRevisionId RevisionId,
    int PageIndex = 0);

public sealed record ExtensionBox(
    DocumentBoxId Id,
    DocumentBoxId? ParentId,
    string Type,
    string? Subtype,
    string? Text,
    string PayloadJson,
    NormalizedBBox? BBox = null,
    int? HeadingLevel = null,
    string? CodeLanguage = null,
    bool Suppressed = false)
{
    public DocumentBoxPayload? Payload { get; init; }
}

public enum ExtensionProgressStatus
{
    Started,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record ExtensionProgress(
    string Phase,
    int Completed,
    int Total,
    string? Message = null,
    ExtensionProgressStatus Status = ExtensionProgressStatus.Running,
    string? ParentPhase = null,
    int? Attempt = null,
    string? ExecutionId = null,
    string? ParentExecutionId = null,
    TimeSpan? Elapsed = null,
    string? ItemId = null);

public sealed record ExtensionGlossaryEntry(string Source, string Translation, string? Note = null);

public sealed record TranslationExtensionOptions(
    int BatchSize = 8,
    int MaxBatchCharacters = 12000,
    int? MaxBatchTokens = null,
    int Concurrency = 4,
    TimeSpan? RequestInterval = null,
    TimeSpan? RequestTimeout = null,
    int MaxRetries = 2,
    int WindowRadius = 0,
    bool BackfillPreviousWindowTranslation = false,
    string? DocumentTitle = null,
    IReadOnlyList<ExtensionGlossaryEntry>? Glossary = null,
    string? PromptContext = null,
    string? ProviderId = null,
    string? Model = null);

/// <summary>
/// A canonical host language tag (for example <c>zh-Hans</c>). The dedicated type is how the
/// compiler and the extension surface recognize a language field: construction goes through
/// <see cref="Create"/> (or the <see cref="Auto"/>/<see cref="Unspecified"/> sentinels), whose
/// constructor path runs the host normalization (<see cref="ItemLanguage.Canonicalize"/>)
/// exactly once at the boundary, so every downstream consumer compares and maps canonical
/// values and no raw user text can reach an engine unnormalized. As with any struct,
/// <c>default(LanguageTag)</c> runs no constructor and reads as unspecified. Each extension
/// then maps the canonical tag to its provider's codes itself; there is deliberately no
/// shared provider mapping.
/// </summary>
public readonly record struct LanguageTag
{
    private LanguageTag(string? value)
    {
        Value = ItemLanguage.Canonicalize(value) ?? string.Empty;
    }

    /// <summary>The canonical tag text; empty when the tag is unspecified.</summary>
    public string Value { get; }

    /// <summary>The auto-detection sentinel; valid for source languages.</summary>
    public static LanguageTag Auto => new("auto");

    /// <summary>The absence of a language; job-level fallbacks decide what replaces it.</summary>
    public static LanguageTag Unspecified => new(null as string);

    /// <summary>True when no language was supplied.</summary>
    public bool IsUnspecified => string.IsNullOrEmpty(Value);

    /// <summary>True for the auto-detection sentinel.</summary>
    public bool IsAuto => string.Equals(Value, "auto", StringComparison.Ordinal);

    /// <summary>Canonicalizes arbitrary host or user text into a <see cref="LanguageTag"/>.</summary>
    public static LanguageTag Create(string? value)
    {
        return new LanguageTag(value);
    }

    public override string ToString()
    {
        return Value ?? string.Empty;
    }
}

/// <summary>
/// Immutable facts about one translation job. The config snapshot is the run-pinned view; the host
/// never changes these values while the job lives.
/// </summary>
public sealed record TranslationJobInfo(
    string JobId,
    DocumentInstanceId DocumentId,
    LanguageTag SourceLanguage,
    LanguageTag TargetLanguage,
    TranslationExtensionOptions Options,
    ExtensionConfigSnapshot Config);

/// <summary>One page the host has frozen for this job. Boxes are read-only source snapshots.</summary>
public sealed record TranslationPageInput(
    ExtensionPageIdentity Page,
    IReadOnlyList<ExtensionBox> Boxes);

/// <summary>One page's extension results. The host validates identity and ownership before commit.</summary>
public sealed record TranslationPageResult(
    ExtensionPageIdentity Page,
    IReadOnlyList<ExtensionBoxOutcome> Outcomes);

/// <summary>The host's decision for one committed or rejected page attempt.</summary>
public enum TranslationPageCommitState
{
    Committed,
    Partial,
    Skipped,
    Failed,

    /// <summary>The page changed concurrently; <see cref="TranslationPageCommit.RetryPage"/> carries a reload.</summary>
    Conflict
}

/// <summary>
/// The result of one <see cref="ITranslationJob.CommitPageAsync"/> call. A <see cref="TranslationPageCommitState.Conflict"/>
/// always carries the reloaded page, and the host has not consumed this attempt's outcomes.
/// </summary>
public sealed record TranslationPageCommit(
    TranslationPageCommitState State,
    int CommittedBoxCount = 0,
    TranslationPageInput? RetryPage = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// The task-level host interface for one translation run. The extension owns page order, batching,
/// concurrency and retry policy; the host owns page identity, revision checks, context snapshots,
/// per-page atomic commit and cancellation.
/// </summary>
public interface ITranslationJob
{
    /// <summary>Run-pinned job facts, including the frozen configuration snapshot.</summary>
    TranslationJobInfo Info { get; }

    /// <summary>Optional progress sink; page-scoped events set <see cref="ExtensionProgress.ItemId"/> to the page index.</summary>
    IProgress<ExtensionProgress>? Progress { get; }

    /// <summary>
    /// Returns the next page that still needs a translation, in request order. Pages that need no work
    /// (no translatable text, already current and not forced, or unreadable) are settled by the host and
    /// skipped here. Returns null when no page remains.
    /// </summary>
    ValueTask<TranslationPageInput?> NextPageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads read-only reference context for a page: neighboring source pages within
    /// <paramref name="neighborRadius"/> and the previously stored translation of the page before it.
    /// The host freezes what it reads for the remainder of the job.
    /// </summary>
    ValueTask<TranslationContextSnapshot> ReadContextAsync(TranslationPageInput page, int neighborRadius,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the currently committed translation of one page, including pages committed earlier in this job.
    /// Returns null when no current translation exists. This is how sequential policies consume the
    /// translation a previous page just committed.
    /// </summary>
    ValueTask<TranslationContextTranslationSnapshot?> GetCommittedPageTranslationAsync(int pageIndex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the page results and commits them in one atomic page write. The host records the page's
    /// terminal outcome; callers must not commit a page twice unless a preceding call returned
    /// <see cref="TranslationPageCommitState.Conflict"/>.
    /// </summary>
    ValueTask<TranslationPageCommit> CommitPageAsync(TranslationPageResult result,
        CancellationToken cancellationToken = default);
}

/// <summary>A neighboring source page frozen for the lifetime of one translation run.</summary>
public sealed record TranslationContextPageSnapshot(ExtensionPageIdentity Page, string SourceText);

/// <summary>A persisted or newly committed neighboring translation and the revision it describes.</summary>
public sealed record TranslationContextTranslationSnapshot(
    ExtensionPageIdentity Page,
    string Markdown,
    LanguageTag TargetLanguage,
    int Version);

/// <summary>Controlled, read-only context supplied by the host; extensions choose how to use it.</summary>
public sealed record TranslationContextSnapshot(
    IReadOnlyList<TranslationContextPageSnapshot> Neighbors,
    TranslationContextTranslationSnapshot? PreviousStoredTranslation = null,
    TranslationContextTranslationSnapshot? PreviousRunTranslation = null);

public enum ExtensionBoxOutcomeKind
{
    Translated,
    Failed,
    Skipped
}

public sealed record ExtensionBoxOutcome(
    DocumentBoxId BoxId,
    ExtensionBoxOutcomeKind Kind,
    string? TranslatedText = null,
    string? TranslatedPayloadJson = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public DocumentBoxPayload? TranslatedPayload { get; init; }

    public static ExtensionBoxOutcome FromPayload(DocumentBoxId boxId, DocumentBoxPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new ExtensionBoxOutcome(boxId, ExtensionBoxOutcomeKind.Translated)
        {
            TranslatedPayload = payload
        };
    }

    public string? GetTranslatedText()
    {
        return TranslatedPayload switch
        {
            TextBoxPayload text => text.Markdown,
            ListBoxPayload list => list.Markdown,
            TableBoxPayload table => TableContentAdapter.GetPlainText(table),
            AlgorithmBoxPayload algorithm => algorithm.Markdown,
            _ => TranslatedText
        };
    }
}

/// <summary>
/// Typed image input for one extension LLM message: the host-owned image bytes and their media
/// type. Bytes are the only payload — never a source or cache path — and the host encodes them
/// into the provider request shape itself.
/// </summary>
public sealed record LlmImage(byte[] Data, string MediaType, string? Detail = null);

public sealed record LlmMessage(string Role, string Content)
{
    /// <summary>Typed images attached to this message. Only <c>user</c> messages may carry images.</summary>
    public IReadOnlyList<LlmImage>? Images { get; init; }
}

public sealed record LlmRequest(
    IReadOnlyList<LlmMessage> Messages,
    string? Model = null,
    decimal? Temperature = null,
    int? MaxOutputTokens = null,
    string? ProviderId = null)
{
    /// <summary>
    /// The capability domain this call belongs to (<c>translation</c>, <c>metadata</c> or <c>ocr</c>);
    /// a domain-bound capability stamps it so unbound requests keep their legacy defaults.
    /// </summary>
    public string? Domain { get; init; }

    /// <summary>
    /// Explicit reasoning effort for this request. When null a domain-bound capability applies its
    /// configured default (for example the OCR reasoning effort); <c>"none"</c> disables reasoning.
    /// </summary>
    public string? ReasoningEffort { get; init; }
}

public sealed record LlmResponse(string Text, string? Model = null, int? InputTokens = null, int? OutputTokens = null);

public interface ILlmCapability
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
}

public interface IExtensionResources
{
    ILlmCapability Llm { get; }
    HttpClient Http { get; }
    string ConfigurationFingerprint => string.Empty;
    IExtensionResources ForRun(ExtensionConfigSnapshot config);

    ILlmCapability ResolveLlm(string? defaultDomain, ExtensionModelSelection? selection)
    {
        return Llm;
    }

    Task WaitToProceedAsync(CancellationToken cancellationToken = default);

    Task<string?> GetCredentialAsync(string reference, CancellationToken cancellationToken = default);
}

/// <summary>
/// One translation engine. There is exactly one entry point: the extension receives the whole task and
/// owns page order, batching, concurrency and retry policy. The host supplies pages and reference context
/// and validates each atomic page commit.
/// </summary>
public interface ITranslationExtension
    : IExtensionDefinition, IExtensionDerivedState
{
    Task RunAsync(ITranslationJob job, IExtensionResources resources,
        CancellationToken cancellationToken = default);
}

public interface IExtensionDefinition
{
    ExtensionDescriptor Descriptor => new("", "", "", "");
    IReadOnlyList<ConfigFieldDescriptor> ConfigFields => Array.Empty<ConfigFieldDescriptor>();
}

public interface IExtensionSettingsDefinition
{
    Type SettingsType { get; }
}

/// <summary>Optional extension-owned cache invalidation for rebuildable derived data.</summary>
public interface IExtensionDerivedState
{
    Task ClearDerivedStateAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}

public interface IExtensionLease : IAsyncDisposable
{
    ExtensionDescriptor Descriptor { get; }
    string Fingerprint { get; }
    ExtensionConfigSnapshot Config { get; }
    IReadOnlyList<ConfigFieldDescriptor> ConfigFields { get; }
    IExtensionDefinition Definition { get; }
    ITranslationExtension Translation { get; }
    IMetadataExtension? Metadata { get; }
    IOcrExtension? Ocr { get; }
    IRetrievalExtension? Retrieval { get; }

    /// <summary>
    /// The pinned PDF text-layer provider, when the generation exports one; null otherwise.
    /// Providers are selected and pinned independently of the image OCR engine.
    /// </summary>
    IPdfTextLayerExtension? PdfTextLayer => null;
}

public interface IMetadataExtension : IExtensionDefinition
{
    MetadataSourceDefinition SourceDefinition { get; }

    Task<Result<MetadataCandidate>> LookupAsync(NormalizedIdentifier identifier,
        ExtensionConfigSnapshot config, IExtensionResources resources,
        CancellationToken cancellationToken = default);
}

/// <summary>Opaque identity for one page image or crop supplied to an OCR extension.</summary>
public readonly record struct OcrInputId
{
    [System.Text.Json.Serialization.JsonConstructor]
    public OcrInputId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }
}

[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record OcrExtensionBoxCandidate(OcrInputId InputId, DocumentBoxSeed Candidate)
{
    /// <summary>Compatibility constructor for text-oriented region OCR extensions.</summary>
    public OcrExtensionBoxCandidate(OcrInputId inputId, NormalizedBBox bbox, LocalOcrCandidate candidate,
        double? confidence = null)
        : this(inputId, CreateTextSeed(bbox, candidate, confidence))
    {
    }

    public NormalizedBBox BBox => Candidate.BBox;

    public double? Confidence => Candidate.Confidence;

    private static DocumentBoxSeed CreateTextSeed(NormalizedBBox bbox, LocalOcrCandidate candidate,
        double? confidence)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new DocumentBoxSeed(null, null, 0, candidate.BoxType, null, bbox, candidate.Payload,
            candidate.HeadingLevel, Confidence: confidence);
    }
}

/// <summary>
/// One page's submission: typed box candidates plus an explicit outcome for every input the page
/// was issued with. <paramref name="Inputs"/> must cover each issued input exactly once, so a
/// successful-but-blank input is distinguishable from a failed one and a partially recognized
/// page is never reported as a full success.
/// </summary>
public sealed record OcrExtensionCandidate(
    ExtensionPageIdentity Page,
    IReadOnlyList<OcrExtensionBoxCandidate> Boxes,
    IReadOnlyList<OcrInputSettlement> Inputs);

public sealed record RetrievalExtensionContext(
    string Query,
    ExtensionConfigSnapshot Config,
    IReadOnlyList<SearchMatchedUnit> Candidates,
    int ResultLimit);

public sealed record RetrievalExtensionMatch(SearchUnitId UnitId, double Score, string? Explanation = null);

public sealed record RetrievalExtensionResult(IReadOnlyList<RetrievalExtensionMatch> Matches);

public interface IRetrievalExtension : IExtensionDefinition
{
    Task<RetrievalExtensionResult> RankAsync(RetrievalExtensionContext context,
        IExtensionResources resources, CancellationToken cancellationToken = default);
}

/// <summary>Host-side validation for extension proposals before domain ownership checks or commits.</summary>
public static class ExtensionCandidateValidation
{
    /// <summary>
    /// Validates one candidate submission against a page the job issued. The candidate page
    /// identity (including its frozen source revision) must equal the issued page exactly; every
    /// box must reference an issued input and keep its geometry inside that input's region;
    /// job-allocated seed <see cref="DocumentBoxSeed.BoxId"/> values must be unique across the job;
    /// a <see cref="DocumentBoxSeed.ParentBoxId"/> must resolve inside the same submission; and a
    /// <see cref="DocumentBoxSeed.ContinuesFromBoxId"/> must resolve inside the same submission or
    /// in <paramref name="previouslyAcceptedBoxIds"/>, which is how cross-page continuations pass
    /// validation. Domain-level checks (known box types, payload rules) still belong to the
    /// document-tree candidate validation downstream.
    /// </summary>
    public static Result ValidateOcr(OcrJobPage page, OcrExtensionCandidate candidate,
        IReadOnlySet<DocumentBoxId>? previouslyAcceptedBoxIds = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Page != page.Page)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "OCR candidate belongs to a different page or source revision.");
        }

        if (page.Inputs is null || page.Inputs.Count == 0 || candidate.Boxes is null ||
            candidate.Inputs is null)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "OCR input and candidate lists are required.");
        }

        Dictionary<OcrInputId, NormalizedBBox> inputs = [];
        foreach (OcrJobInput input in page.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.ContentType) ||
                string.IsNullOrWhiteSpace(input.Id.Value))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "OCR inputs require an identity and a content type.");
            }

            if (!inputs.TryAdd(input.Id, input.Region))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed, "OCR input identities must be unique.");
            }

            Result validInputBox = input.Region.Validate();
            if (validInputBox.IsFailure)
            {
                return validInputBox;
            }
        }

        // Every issued input must be accounted for exactly once: no foreign or duplicate ids,
        // a failed input must carry an error code, and a recognized/skipped input carries none.
        Dictionary<OcrInputId, OcrInputSettlementState> settlements =
            new(candidate.Inputs.Count);
        foreach (OcrInputSettlement input in candidate.Inputs)
        {
            if (input is null || !inputs.ContainsKey(input.InputId))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "OCR input outcomes must reference inputs issued for this page.");
            }

            if (!settlements.TryAdd(input.InputId, input.State))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "OCR input outcomes must declare each issued input exactly once.");
            }

            switch (input.State)
            {
                case OcrInputSettlementState.Recognized or OcrInputSettlementState.Skipped
                    when input.ErrorCode is not null || input.ErrorMessage is not null:
                case OcrInputSettlementState.Failed
                    when string.IsNullOrWhiteSpace(input.ErrorCode):
                case not (OcrInputSettlementState.Recognized or OcrInputSettlementState.Failed or
                    OcrInputSettlementState.Skipped):
                    return Result.Failure(AppErrorCodes.ValidationFailed,
                        "OCR input outcomes must be Recognized, Skipped, or Failed with an error code.");
            }
        }

        if (settlements.Count != inputs.Count)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "OCR input outcomes must account for every issued input.");
        }

        HashSet<DocumentBoxId> declaredBoxIds = [];
        HashSet<DocumentBoxId> parentReferences = [];
        HashSet<DocumentBoxId> continuationReferences = [];
        foreach (OcrExtensionBoxCandidate box in candidate.Boxes)
        {
            DocumentBoxSeed seed = box.Candidate;
            if (seed?.BoxId is { } declared && !declaredBoxIds.Add(declared))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "OCR candidate box identities must be unique within a submission.");
            }

            if (seed?.ParentBoxId is { } parent)
            {
                parentReferences.Add(parent);
            }

            if (seed?.ContinuesFromBoxId is { } continuesFrom)
            {
                continuationReferences.Add(continuesFrom);
            }
        }

        if (previouslyAcceptedBoxIds is not null)
        {
            foreach (DocumentBoxId declared in declaredBoxIds)
            {
                if (previouslyAcceptedBoxIds.Contains(declared))
                {
                    return Result.Failure(AppErrorCodes.ValidationFailed,
                        "OCR candidate box identities must not reuse ids accepted earlier in the job.");
                }
            }
        }

        foreach (OcrExtensionBoxCandidate box in candidate.Boxes)
        {
            if (!inputs.TryGetValue(box.InputId, out NormalizedBBox inputBox))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "OCR candidate references an input that was not supplied to the extension.");
            }

            if (settlements[box.InputId] != OcrInputSettlementState.Recognized)
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "OCR boxes may only attach to inputs declared recognized.");
            }

            if (box.Candidate is null ||
                (box.Candidate.Payload is null && box.Candidate.BoxType != DocumentBoxType.LogicalPage))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed, "OCR candidate content is required.");
            }

            Result validBox = box.BBox.Validate();
            if (validBox.IsFailure)
            {
                return validBox;
            }

            if (box.BBox.X < inputBox.X || box.BBox.Y < inputBox.Y
                                        || box.BBox.X + box.BBox.Width > inputBox.X + inputBox.Width
                                        || box.BBox.Y + box.BBox.Height > inputBox.Y + inputBox.Height)
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "OCR candidate geometry must remain within its supplied input region.");
            }

            if (box.Confidence is double confidence &&
                (!double.IsFinite(confidence) || confidence < 0 || confidence > 1))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed, "OCR confidence must be within 0..1.");
            }
        }

        foreach (DocumentBoxId parent in parentReferences)
        {
            if (!declaredBoxIds.Contains(parent))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "OCR parent references must resolve inside the same page submission.");
            }
        }

        foreach (DocumentBoxId continuesFrom in continuationReferences)
        {
            if (!declaredBoxIds.Contains(continuesFrom) &&
                !(previouslyAcceptedBoxIds?.Contains(continuesFrom) ?? false))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "OCR continuation references must resolve inside the job's accepted or submitted boxes.");
            }
        }

        return Result.Success();
    }

    public static Result ValidateRetrieval(RetrievalExtensionContext context, RetrievalExtensionResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);
        if (context.Candidates is null || result.Matches is null)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "Retrieval candidates and result matches are required.");
        }

        if (context.ResultLimit < 0 || result.Matches.Count > context.ResultLimit)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "Retrieval result exceeds the host supplied result limit.");
        }

        HashSet<SearchUnitId> allowed = [];
        foreach (SearchMatchedUnit candidate in context.Candidates)
        {
            if (!allowed.Add(candidate.UnitId))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "Host supplied retrieval candidate identities must be unique.");
            }
        }

        HashSet<SearchUnitId> returned = [];
        foreach (RetrievalExtensionMatch match in result.Matches)
        {
            if (!allowed.Contains(match.UnitId) || !returned.Add(match.UnitId))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "Retrieval results must reference unique units supplied by the host.");
            }

            if (!double.IsFinite(match.Score))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed, "Retrieval score must be finite.");
            }
        }

        return Result.Success();
    }
}

public sealed record ExtensionGenerationInfo(
    string Id,
    string Fingerprint,
    bool IsActive,
    bool IsLastKnownGood,
    int LeaseCount,
    IReadOnlyList<ExtensionDiagnostic> Diagnostics);

public interface IExtensionManager : IAsyncDisposable
{
    Task<ExtensionCheckResult> CheckAsync(ExtensionPackageSnapshot package,
        CancellationToken cancellationToken = default);

    Task<ExtensionCheckResult> LoadAsync(ExtensionPackageSnapshot package,
        CancellationToken cancellationToken = default);

    Task<ExtensionCheckResult> ActivateAsync(string id, ExtensionPackageSnapshot package,
        CancellationToken cancellationToken = default);

    Task SaveSourceAsync(string id, IReadOnlyDictionary<string, string> sources,
        CancellationToken cancellationToken = default);

    Task SavePackageAsync(ExtensionPackageSnapshot package, CancellationToken cancellationToken = default)
    {
        if (package.BinaryFiles.Count != 0)
        {
            throw new NotSupportedException("This extension store cannot persist binary package dependencies.");
        }

        return SaveSourceAsync(package.Id, package.Sources, cancellationToken);
    }

    Task<ExtensionPackageSnapshot> ReadSourceAsync(string id, CancellationToken cancellationToken = default);
    ValueTask<IExtensionLease?> AcquireAsync(string id, CancellationToken cancellationToken = default);
    IReadOnlyList<ExtensionGenerationInfo> List();
    Task SaveConfigAsync(string id, ExtensionConfigSnapshot config, CancellationToken cancellationToken = default);
    Task<ExtensionConfigSnapshot> ReadConfigAsync(string id, CancellationToken cancellationToken = default);
    Task<bool> RestoreAsync(string id, CancellationToken cancellationToken = default);
    Task<bool> PromoteLastKnownGoodAsync(string id, CancellationToken cancellationToken = default);
    Task<bool> RollbackAsync(string id, CancellationToken cancellationToken = default);
}
