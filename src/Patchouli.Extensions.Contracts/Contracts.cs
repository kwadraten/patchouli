namespace Patchouli.Extensions.Contracts;

using Core.Ids;
using Core.Layout;
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
    Capability
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
    IReadOnlyList<string>? Choices = null);

public static class ExtensionHostApi
{
    public const string Current = "patchouli.extensions/1";
}

public enum ExtensionCapabilityKind
{
    Llm,
    Http
}

/// <summary>A package's static request for a managed capability; host authorization is stored separately.</summary>
public sealed record ExtensionCapabilityRequest(
    ExtensionCapabilityKind Kind,
    string Key,
    string? DefaultDomain = null);

public enum ExtensionKind
{
    Translation,
    Metadata,
    Ocr,
    Retrieval
}

public enum ExtensionPageExecutionMode
{
    BoundedParallel,
    Sequential
}

public sealed record ExtensionDescriptor(
    string Id,
    string Name,
    string Version,
    string Description,
    ExtensionKind Kind = ExtensionKind.Translation,
    ExtensionPageExecutionMode PageExecutionMode = ExtensionPageExecutionMode.BoundedParallel)
{
    public string HostApi { get; init; } = ExtensionHostApi.Current;
    public IReadOnlyList<ExtensionCapabilityRequest> CapabilityRequests { get; init; } = [];

    public ExtensionDescriptor WithCapabilityRequests(IReadOnlyList<ExtensionCapabilityRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return this with
        {
            CapabilityRequests = Array.AsReadOnly(requests.ToArray())
        };
    }
}

/// <summary>An immutable, content-addressed package view. Source keys are normalized relative paths.</summary>
public sealed class ExtensionPackageSnapshot
{
    private static readonly StringComparer SourcePathComparer = StringComparer.OrdinalIgnoreCase;

    public ExtensionPackageSnapshot(string id, IReadOnlyDictionary<string, string> sources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(sources);
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Extension id must be a stable alphanumeric identifier.", nameof(id));
        }

        Dictionary<string, string> normalized = new(SourcePathComparer);
        foreach ((string path, string content) in sources)
        {
            string canonicalPath = NormalizeSourcePath(path);
            ArgumentNullException.ThrowIfNull(content);
            if (!normalized.TryAdd(canonicalPath, content))
            {
                throw new ArgumentException($"Duplicate extension source path after normalization: {canonicalPath}",
                    nameof(sources));
            }
        }

        Id = id;
        Sources = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(normalized, StringComparer.Ordinal));
        Fingerprint = PackageFingerprint.Compute(Sources);
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

    public string Id { get; }
    public IReadOnlyDictionary<string, string> Sources { get; }
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
}

public sealed record ExtensionCheckResult(
    bool Succeeded,
    IReadOnlyList<ConfigFieldDescriptor> ConfigFields,
    IReadOnlyList<ExtensionDiagnostic> Diagnostics,
    IReadOnlyList<ExtensionCapabilityRequest>? CapabilityRequests = null);

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

public sealed record TranslationExtensionContext(
    ExtensionPageIdentity Page,
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<ExtensionBox> Boxes,
    ExtensionConfigSnapshot Config,
    IProgress<ExtensionProgress>? Progress = null,
    IReadOnlyList<string>? ReferenceContexts = null,
    TranslationExtensionOptions? Options = null,
    TranslationContextSnapshot? ContextSnapshot = null)
{
    public TranslationExtensionContext WithReferenceContexts(IReadOnlyList<string>? references)
    {
        return this with { ReferenceContexts = references };
    }
}

/// <summary>A neighboring source page frozen for the lifetime of one translation run.</summary>
public sealed record TranslationContextPageSnapshot(ExtensionPageIdentity Page, string SourceText);

/// <summary>A persisted or newly committed neighboring translation and the revision it describes.</summary>
public sealed record TranslationContextTranslationSnapshot(
    ExtensionPageIdentity Page,
    string Markdown,
    string? TargetLanguage,
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

public sealed record TranslationExtensionResult(IReadOnlyList<ExtensionBoxOutcome> Outcomes);

public sealed record LlmMessage(string Role, string Content);

public sealed record LlmRequest(
    IReadOnlyList<LlmMessage> Messages,
    string? Model = null,
    decimal? Temperature = null,
    int? MaxOutputTokens = null,
    string? ProviderId = null);

public sealed record LlmResponse(string Text, string? Model = null, int? InputTokens = null, int? OutputTokens = null);

public interface ILlmCapability
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
}

public sealed record HttpRequest(
    string Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string? Body = null,
    string? CredentialReference = null);

public sealed record HttpResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, string Body);

public interface IHttpCapability
{
    Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default);
}

public interface IExtensionCapabilities
{
    ILlmCapability Llm { get; }
    IHttpCapability Http { get; }
    string ConfigurationFingerprint => string.Empty;
    IExtensionCapabilities ForRun(ExtensionConfigSnapshot config);

    ILlmCapability ResolveLlm(string? defaultDomain, ExtensionModelSelection? selection)
    {
        return Llm;
    }

    IHttpCapability ResolveHttp(string? defaultDomain, ExtensionConfigSnapshot config)
    {
        return Http;
    }

    ILlmCapability ResolveLlmSlot(string key, string? defaultDomain, ExtensionModelSelection? selection)
    {
        return ResolveLlm(defaultDomain, selection);
    }

    IHttpCapability ResolveHttpSlot(string key, string? defaultDomain, ExtensionConfigSnapshot config)
    {
        return ResolveHttp(defaultDomain, config);
    }

    Task WaitToProceedAsync(CancellationToken cancellationToken = default);
}

public interface ITranslationExtension
    : IExtensionDefinition, IExtensionDerivedState
{
    Task<TranslationExtensionResult> TranslateAsync(TranslationExtensionContext context,
        IExtensionCapabilities capabilities, CancellationToken cancellationToken = default);
}

public interface IExtensionDefinition
{
    ExtensionDescriptor Descriptor { get; }
    IReadOnlyList<ConfigFieldDescriptor> ConfigFields { get; }
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
}

public interface IMetadataExtension : IExtensionDefinition
{
    MetadataSourceDefinition SourceDefinition { get; }

    Task<Result<MetadataCandidate>> LookupAsync(NormalizedIdentifier identifier,
        ExtensionConfigSnapshot config, IExtensionCapabilities capabilities,
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

public sealed record ExtensionOcrInput(
    OcrInputId Id,
    NormalizedBBox BBox,
    string ContentType,
    ReadOnlyMemory<byte> ImageData);

public sealed record OcrExtensionContext(
    ExtensionPageIdentity Page,
    IReadOnlyList<ExtensionOcrInput> Inputs,
    ExtensionConfigSnapshot Config);

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

public sealed record OcrExtensionCandidate(
    ExtensionPageIdentity Page,
    IReadOnlyList<OcrExtensionBoxCandidate> Boxes);

public interface IOcrExtension : IExtensionDefinition
{
    Task<OcrExtensionCandidate> RecognizeAsync(OcrExtensionContext context,
        IExtensionCapabilities capabilities, CancellationToken cancellationToken = default);
}

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
        IExtensionCapabilities capabilities, CancellationToken cancellationToken = default);
}

/// <summary>Host-side validation for extension proposals before domain ownership checks or commits.</summary>
public static class ExtensionCandidateValidation
{
    public static Result ValidateOcr(OcrExtensionContext context, OcrExtensionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Page != context.Page)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "OCR candidate belongs to a different page or source revision.");
        }

        if (context.Inputs is null || candidate.Boxes is null)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "OCR input and candidate lists are required.");
        }

        Dictionary<OcrInputId, NormalizedBBox> inputs = [];
        foreach (ExtensionOcrInput input in context.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.ContentType) || input.ImageData.IsEmpty)
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "OCR inputs require a content type and non-empty image data.");
            }

            if (!inputs.TryAdd(input.Id, input.BBox))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed, "OCR input identities must be unique.");
            }

            Result validInputBox = input.BBox.Validate();
            if (validInputBox.IsFailure)
            {
                return validInputBox;
            }
        }

        foreach (OcrExtensionBoxCandidate box in candidate.Boxes)
        {
            if (!inputs.TryGetValue(box.InputId, out NormalizedBBox inputBox))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "OCR candidate references an input that was not supplied to the extension.");
            }

            if (box.Candidate is null || box.Candidate.Payload is null)
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

    Task<ExtensionPackageSnapshot> ReadSourceAsync(string id, CancellationToken cancellationToken = default);
    ValueTask<IExtensionLease?> AcquireAsync(string id, CancellationToken cancellationToken = default);
    IReadOnlyList<ExtensionGenerationInfo> List();
    Task SaveConfigAsync(string id, ExtensionConfigSnapshot config, CancellationToken cancellationToken = default);
    Task<ExtensionConfigSnapshot> ReadConfigAsync(string id, CancellationToken cancellationToken = default);
    Task<bool> RestoreAsync(string id, CancellationToken cancellationToken = default);
    Task<bool> PromoteLastKnownGoodAsync(string id, CancellationToken cancellationToken = default);
    Task<bool> RollbackAsync(string id, CancellationToken cancellationToken = default);
}
