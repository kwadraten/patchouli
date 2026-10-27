using System.Globalization;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr;

namespace Patchouli.Host.Extensions;

/// <summary>
/// A pinned OCR or retrieval invocation. The caller retains source-version and commit authority.
/// Disposing the run waits for invocations already executing to exit before the generation lease
/// is released — the lease is never dropped underneath live extension code — and once disposal
/// starts, new invocations are rejected.
/// </summary>
public sealed class ExtensionDomainRun : IAsyncDisposable
{
    private readonly IExtensionLease _lease;
    private readonly IExtensionResources _capabilities;
    private readonly ExtensionConfigSnapshot _config;
    private readonly object _disposeGate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private int _lifecycle; // 0 = running, 1 = draining, 2 = drained
    private Task? _disposeTask;

    internal ExtensionDomainRun(IExtensionLease lease, IExtensionResources capabilities,
        ExtensionConfigSnapshot config)
    {
        _lease = lease;
        _capabilities = capabilities;
        _config = config;
    }

    public string Fingerprint => _lease.Fingerprint;
    public string PackageId => _lease.Descriptor.Id;
    public ExtensionKind Kind => _lease.Descriptor.Kind;
    public ExtensionConfigSnapshot Config => _config;
    internal IReadOnlyList<ConfigFieldDescriptor> ConfigFields => _lease.ConfigFields;
    internal IOcrExtension? OcrExtension => _lease.Ocr;

    /// <summary>
    /// Non-executing readiness probe against this run's pinned configuration: model files,
    /// credentials and endpoints only — never starts a model session or renders. Runs inside the
    /// invocation gate so a probe in flight also holds the generation lease.
    /// </summary>
    public async Task<Result> CheckOcrReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnterInvocation();
        try
        {
            IOcrExtension extension = _lease.Ocr
                                      ?? throw new InvalidOperationException(
                                          "The selected extension does not implement OCR.");
            return await extension.CheckReadinessAsync(_config, _capabilities, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ExitInvocation();
        }
    }

    /// <summary>
    /// Invokes the extension's single task-level entry point once over a host-built
    /// <see cref="IOcrJob"/>. The extension owns input order and retry policy; the host issues
    /// pages, materializes images on demand under a bounded budget and settles each page exactly
    /// once. The extension call always runs on the thread pool — an <see cref="IOcrJob"/> is a
    /// mediation surface, not a thread boundary, so a synchronously blocking engine cannot stall
    /// the caller's synchronization context. When the call returns — normally, faulted or
    /// cancelled — every requested page has a terminal settlement, and the invocation then
    /// drains any input-image read the extension dropped: a loader that ignores the job's
    /// cancellation keeps this invocation — and the generation lease — alive until it returns.
    /// </summary>
    public async Task<Result<OcrDomainRunResult>> RunOcrAsync(OcrDomainJobRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnterInvocation();
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            IOcrExtension extension = _lease.Ocr
                                      ?? throw new InvalidOperationException(
                                          "The selected extension does not implement OCR.");
            Result validRequest = HostOcrJob.ValidateRequest(request);
            if (validRequest.IsFailure)
            {
                return Result<OcrDomainRunResult>.Failure(validRequest.ErrorCode!, validRequest.ErrorMessage!);
            }

            HostOcrJob job = new(request, _config);
            try
            {
                await Task.Run(() => extension.RunAsync(job, _capabilities, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                await job.CompleteAsync(cancellationToken.IsCancellationRequested)
                    .ConfigureAwait(false);
            }

            return Result<OcrDomainRunResult>.Success(new OcrDomainRunResult(job.AcceptedCandidates,
                job.Settlements));
        }
        finally
        {
            ExitInvocation();
        }
    }

    public async Task<Result<RetrievalExtensionResult>> RankAsync(RetrievalExtensionContext input,
        CancellationToken cancellationToken = default)
    {
        EnterInvocation();
        try
        {
            IRetrievalExtension extension = _lease.Retrieval
                                            ?? throw new InvalidOperationException(
                                                "The selected extension does not implement retrieval.");
            RetrievalExtensionContext context = input with
            {
                Config = _config,
                Candidates = Array.AsReadOnly(input.Candidates.ToArray())
            };
            Result validInput =
                ExtensionCandidateValidation.ValidateRetrieval(context, new RetrievalExtensionResult([]));
            if (validInput.IsFailure)
            {
                return Result<RetrievalExtensionResult>.Failure(validInput.ErrorCode!, validInput.ErrorMessage!);
            }

            RetrievalExtensionResult candidate = await extension.RankAsync(context, _capabilities, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Result valid = ExtensionCandidateValidation.ValidateRetrieval(context, candidate);
            return valid.IsSuccess
                ? Result<RetrievalExtensionResult>.Success(candidate with
                {
                    Matches = Array.AsReadOnly(candidate.Matches.ToArray())
                })
                : Result<RetrievalExtensionResult>.Failure(valid.ErrorCode!, valid.ErrorMessage!);
        }
        finally
        {
            ExitInvocation();
        }
    }

    /// <summary>
    /// Stops new invocations, waits for any invocation in flight to exit, then releases the
    /// generation lease exactly once. Every caller — serial or concurrent — awaits the same
    /// drain-plus-lease-release operation, including its failure; a second disposer never
    /// returns while the first lease disposal is still running.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                _lifecycle = 1;
                // Lease callbacks may execute synchronously; do not invoke them under this lock.
                _disposeTask = Task.Run(DisposeCore);
            }

            task = _disposeTask;
        }

        return new ValueTask(task);
    }

    private async Task DisposeCore()
    {
        Task? wait;
        lock (_disposeGate)
        {
            if (_lifecycle == 0)
            {
                _lifecycle = 1;
            }

            wait = _active > 0 ? _drained.Task : null;
        }

        if (wait is not null)
        {
            await wait.ConfigureAwait(false);
        }

        await _lease.DisposeAsync().ConfigureAwait(false);
    }

    private void EnterInvocation()
    {
        lock (_disposeGate)
        {
            if (_lifecycle != 0)
            {
                throw new ObjectDisposedException(nameof(ExtensionDomainRun));
            }

            _active++;
        }
    }

    private void ExitInvocation()
    {
        lock (_disposeGate)
        {
            if (--_active == 0 && _lifecycle == 1)
            {
                _lifecycle = 2;
                _drained.TrySetResult();
            }
        }
    }

    /// <summary>
    /// The host-mediated job handed to <see cref="IOcrExtension.RunAsync"/>. One instance serves one
    /// invocation: it issues pages in request order, materializes input images on demand, enforces
    /// exactly-once settlement per page, and settles whatever the extension left unfinished when the
    /// invocation ends. Image leases the extension never disposed are released here deterministically.
    /// </summary>
    internal sealed class HostOcrJob : IOcrJob
    {
        private readonly object _gate = new();
        private readonly IReadOnlyList<OcrJobPage> _pages;
        private readonly Func<OcrJobInput, CancellationToken, ValueTask<OcrInputImage>> _readImage;
        private readonly SemaphoreSlim _imageBudget;
        private readonly Dictionary<PageId, OcrJobPage> _issued = [];
        private readonly Dictionary<OcrInputId, OcrJobInput> _issuedInputs = [];
        private readonly Dictionary<OcrInputId, PageId> _inputPages = [];
        private readonly Dictionary<PageId, OcrPageSettlement> _settlements = [];
        private readonly HashSet<DocumentBoxId> _acceptedBoxIds = [];
        private readonly HashSet<PageId> _conflictedPages = [];
        private readonly List<OcrExtensionCandidate> _accepted = [];
        private readonly Dictionary<Guid, OcrInputImage> _outstandingImages = [];
        private readonly CancellationTokenSource _readLifetime = new();

        private readonly Func<ExtensionPageIdentity, CancellationToken, ValueTask<ExtensionPageIdentity?>>?
            _baselineProbe;

        private readonly TaskCompletionSource _readsDrained =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _nextIndex;
        private int _activeReads;
        private bool _running = true;

        internal HostOcrJob(OcrDomainJobRequest request, ExtensionConfigSnapshot config)
        {
            // Copy the caller's containers: the request's page and input lists are caller-owned
            // and must not be able to mutate a running job's frozen selection. Each issued page
            // exposes a read-only input view — an extension must not cast an array-backed Inputs
            // list back and rewrite descriptors the job tracks by identity.
            _pages = request.Pages
                .Select(page => new OcrJobPage(page.Page, Array.AsReadOnly(page.Inputs.ToArray())))
                .ToArray();
            _readImage = request.ReadImage;
            _imageBudget = new SemaphoreSlim(request.MaxInFlightImages);
            Info = new OcrJobInfo(request.JobId, request.DocumentId, request.Options ?? new OcrJobOptions(),
                config);
            Progress = request.Progress;
            _baselineProbe = request.BaselineProbe;
        }

        public OcrJobInfo Info { get; }
        public IProgress<ExtensionProgress>? Progress { get; }

        public IReadOnlyList<OcrExtensionCandidate> AcceptedCandidates
        {
            get
            {
                lock (_gate)
                {
                    return _accepted.ToArray();
                }
            }
        }

        /// <summary>Every requested page's terminal outcome in request order.</summary>
        public IReadOnlyList<OcrPageSettlement> Settlements
        {
            get
            {
                lock (_gate)
                {
                    return _pages.Select(page => _settlements[page.Page.PageId]).ToArray();
                }
            }
        }

        internal static Result ValidateRequest(OcrDomainJobRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.JobId))
            {
                return Result.Failure(AppErrorCodes.InvalidArgument, "An OCR job requires a job id.");
            }

            if (request.Pages is null || request.Pages.Count == 0)
            {
                return Result.Failure(AppErrorCodes.InvalidArgument,
                    "An OCR job requires at least one page.");
            }

            if (request.ReadImage is null)
            {
                return Result.Failure(AppErrorCodes.InvalidArgument,
                    "An OCR job requires an image reader.");
            }

            if (request.MaxInFlightImages < 1)
            {
                return Result.Failure(AppErrorCodes.InvalidArgument,
                    "An OCR job requires an in-flight image budget of at least one.");
            }

            if (request.Options is { Concurrency: < 1 })
            {
                return Result.Failure(AppErrorCodes.InvalidArgument,
                    "An OCR job requires an SDK concurrency budget of at least one.");
            }

            HashSet<PageId> pageIds = [];
            HashSet<OcrInputId> inputIds = [];
            foreach (OcrJobPage page in request.Pages)
            {
                if (page is null || page.Page is null || !pageIds.Add(page.Page.PageId))
                {
                    return Result.Failure(AppErrorCodes.InvalidArgument,
                        "OCR job pages must be unique and non-null.");
                }

                if (page.Page.DocumentId != request.DocumentId)
                {
                    return Result.Failure(AppErrorCodes.InvalidArgument,
                        "OCR job pages must belong to the job's document instance.");
                }

                if (page.Inputs is null || page.Inputs.Count == 0)
                {
                    return Result.Failure(AppErrorCodes.InvalidArgument,
                        "Every OCR job page requires at least one input.");
                }

                foreach (OcrJobInput input in page.Inputs)
                {
                    if (input is null || string.IsNullOrWhiteSpace(input.Id.Value) ||
                        string.IsNullOrWhiteSpace(input.ContentType) || !inputIds.Add(input.Id))
                    {
                        return Result.Failure(AppErrorCodes.InvalidArgument,
                            "OCR job inputs must be unique and declare an identity and content type.");
                    }

                    Result validRegion = input.Region.Validate();
                    if (validRegion.IsFailure)
                    {
                        return validRegion;
                    }
                }
            }

            return Result.Success();
        }

        public ValueTask<OcrJobPage?> NextPageAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrJobPage page;
            lock (_gate)
            {
                EnsureRunning();
                if (_nextIndex >= _pages.Count)
                {
                    return ValueTask.FromResult<OcrJobPage?>(null);
                }

                page = _pages[_nextIndex++];
                _issued[page.Page.PageId] = page;
                foreach (OcrJobInput input in page.Inputs)
                {
                    _issuedInputs[input.Id] = input;
                    _inputPages[input.Id] = page.Page.PageId;
                }
            }

            ReportPage(page, ExtensionProgressStatus.Started);
            return ValueTask.FromResult<OcrJobPage?>(page);
        }

        /// <summary>
        /// Materializes one issued input under the job's in-flight image budget. The call accepts
        /// only the descriptor instance the job issued and only while its page is still unsettled.
        /// Every call registers under the same gate that closes the job, so completion can drain
        /// real in-flight reads instead of releasing around them; a read that finishes after the
        /// job closed disposes its own image and returns its budget permit exactly once.
        /// </summary>
        public async ValueTask<OcrInputImage> ReadInputImageAsync(OcrJobInput input,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(input);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                EnsureUsable(input);
                // Registered before any await under the same lock CompleteAsync takes: a read
                // that began before closure is tracked until it exits, so completion drains it.
                _activeReads++;
            }

            // The job-owned read lifetime cancels budget waiters and cooperative loaders when
            // the job ends; a loader that ignores it still holds the read (and the invocation)
            // open until it actually returns.
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                _readLifetime.Token, cancellationToken);
            bool permitHeld = false;
            OcrInputImage? loaded = null;
            try
            {
                await _imageBudget.WaitAsync(linked.Token).ConfigureAwait(false);
                permitHeld = true;
                lock (_gate)
                {
                    EnsureUsable(input);
                }

                loaded = await _readImage(input, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    // Re-check after the load: the page may have settled or the job ended while
                    // the loader was in flight.
                    EnsureUsable(input);
                }

                ArgumentNullException.ThrowIfNull(loaded);
                ReadOnlyMemory<byte> data;
                try
                {
                    data = loaded.Data;
                }
                catch (ObjectDisposedException)
                {
                    throw new InvalidOperationException(
                        "The OCR image reader returned an already-disposed image.");
                }

                if (loaded.Id != input.Id || loaded.ContentType != input.ContentType || data.IsEmpty)
                {
                    throw new InvalidOperationException(
                        "The OCR image reader returned an image that does not match the issued input.");
                }

                Guid leaseId = Guid.NewGuid();
                ImageLeaseRelease release = new(this, leaseId, loaded);
                OcrInputImage tracked = new(loaded.Id, loaded.ContentType, data, release.Release);
                loaded = null;

                // The permit and the buffer are owned by the lease's release action from here.
                permitHeld = false;
                bool registered;
                lock (_gate)
                {
                    // Register under the same lock CompleteAsync takes: either the lease lands in
                    // _outstandingImages before completion snapshots it (and completion disposes
                    // it), or the job already ended or the page settled and this read disposes
                    // the lease itself. It is never added to tracking on the failure path, so
                    // the read still owns it.
                    registered = _running && !_settlements.ContainsKey(_inputPages[input.Id]) &&
                                 !linked.IsCancellationRequested;
                    if (registered)
                    {
                        _outstandingImages.Add(leaseId, tracked);
                    }
                }

                if (!registered)
                {
                    // Dispose outside the gate: the lease's release action runs the loader's
                    // callback, and host-owned callbacks must never execute under _gate.
                    tracked.Dispose();
                    linked.Token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("The OCR job is no longer running.");
                }

                return tracked;
            }
            finally
            {
                try
                {
                    try
                    {
                        loaded?.Dispose();
                    }
                    finally
                    {
                        if (permitHeld)
                        {
                            _imageBudget.Release();
                        }
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        if (--_activeReads == 0 && !_running)
                        {
                            _readsDrained.TrySetResult();
                        }
                    }
                }
            }
        }

        public async ValueTask<OcrSubmissionReceipt> SubmitCandidatesAsync(OcrExtensionCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(candidate);
            OcrJobPage page;
            int failedInputs;
            int acceptedBoxCount;
            lock (_gate)
            {
                EnsureRunning();
                OcrSubmissionReceipt? rejected = RejectUnlessIssued(candidate.Page);
                if (rejected is not null)
                {
                    return rejected;
                }

                page = _issued[candidate.Page.PageId];
                Result valid = ExtensionCandidateValidation.ValidateOcr(page, candidate, _acceptedBoxIds);
                if (valid.IsFailure)
                {
                    return OcrSubmissionReceipt.Rejected(valid.ErrorCode!, valid.ErrorMessage!);
                }
            }

            if (_baselineProbe is not null)
            {
                ExtensionPageIdentity? moved =
                    await _baselineProbe(candidate.Page, cancellationToken).ConfigureAwait(false);
                if (moved is not null)
                {
                    // The committed baseline moved under this job. Rebasing an image-derived
                    // result onto a newer (possibly manual) edit would silently overwrite the
                    // user's correction, so the conflict is terminal: the page records the real
                    // conflict code at closure rather than a generic unsettled outcome, and no
                    // retry page is offered.
                    lock (_gate)
                    {
                        OcrSubmissionReceipt? rejected = RejectUnlessIssued(candidate.Page);
                        if (rejected is not null)
                        {
                            return rejected;
                        }

                        _conflictedPages.Add(candidate.Page.PageId);
                    }

                    return new OcrSubmissionReceipt(OcrSubmissionState.Conflict, 0,
                        OcrSettlementCodes.SourceRevisionConflict,
                        "The page's committed revision changed while the job was running; the " +
                        "recognized result was not applied over the newer source.");
                }
            }

            lock (_gate)
            {
                EnsureRunning();
                OcrSubmissionReceipt? rejected = RejectUnlessIssued(candidate.Page);
                if (rejected is not null)
                {
                    return rejected;
                }

                OcrExtensionCandidate accepted = candidate with
                {
                    Boxes = Array.AsReadOnly(candidate.Boxes.ToArray()),
                    Inputs = Array.AsReadOnly(candidate.Inputs.ToArray())
                };
                failedInputs = accepted.Inputs.Count(input =>
                    input.State != OcrInputSettlementState.Recognized);
                acceptedBoxCount = accepted.Boxes.Count;
                _accepted.Add(accepted);
                foreach (DocumentBoxSeed seed in accepted.Boxes.Select(box => box.Candidate))
                {
                    if (seed?.BoxId is { } declared)
                    {
                        _acceptedBoxIds.Add(declared);
                    }
                }

                _settlements[page.Page.PageId] = new OcrPageSettlement(page.Page,
                    OcrPageSettlementState.Accepted, AcceptedBoxCount: accepted.Boxes.Count,
                    FailedInputCount: failedInputs);
            }

            ReportPage(page, failedInputs == 0 ? ExtensionProgressStatus.Completed : ExtensionProgressStatus.Failed);
            return OcrSubmissionReceipt.Accepted(acceptedBoxCount, failedInputs);
        }

        public ValueTask<OcrSubmissionReceipt> FailPageAsync(ExtensionPageIdentity page, string errorCode,
            string? errorMessage = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(page);
            ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
            OcrJobPage issued;
            lock (_gate)
            {
                EnsureRunning();
                OcrSubmissionReceipt? rejected = RejectUnlessIssued(page);
                if (rejected is not null)
                {
                    return ValueTask.FromResult(rejected);
                }

                issued = _issued[page.PageId];
                _settlements[page.PageId] = new OcrPageSettlement(page, OcrPageSettlementState.Failed,
                    errorCode, errorMessage, FailedInputCount: issued.Inputs.Count);
            }

            ReportPage(issued, ExtensionProgressStatus.Failed);
            return ValueTask.FromResult(new OcrSubmissionReceipt(OcrSubmissionState.Failed, 0,
                errorCode, errorMessage, FailedInputCount: issued.Inputs.Count));
        }

        private void ReportPage(OcrJobPage page, ExtensionProgressStatus status)
        {
            try
            {
                // Lifecycle progress remains available even for engines that implement RunAsync
                // directly instead of using the SDK driver. Publication never owns settlement.
                Progress?.Report(new ExtensionProgress("page-ocr",
                    status == ExtensionProgressStatus.Started ? 0 : page.Inputs.Count,
                    page.Inputs.Count, Status: status,
                    ItemId: page.Page.PageIndex.ToString(CultureInfo.InvariantCulture)));
            }
            catch (Exception exception)
            {
                UnexpectedExceptionReporter.Report(exception, "host.ocr-job-progress");
            }
        }

        /// <summary>
        /// Closes the job: issued pages still unsettled settle as cancelled on cancellation and
        /// failed otherwise; pages never taken settle as cancelled. Outstanding image leases are
        /// disposed so buffers cannot outlive the invocation, and the job's read lifetime is
        /// cancelled so budget waiters and cooperative loaders stop. The invocation then waits
        /// until every read call that registered before closure has actually exited — a loader
        /// that ignores cancellation keeps the invocation (and the generation lease) alive until
        /// it returns; an image it returns after closure is disposed by the read itself.
        /// </summary>
        internal async Task CompleteAsync(bool cancelled)
        {
            OcrInputImage[] leaked;
            lock (_gate)
            {
                if (!_running)
                {
                    return;
                }

                _running = false;
                foreach (OcrJobPage page in _pages)
                {
                    if (_settlements.ContainsKey(page.Page.PageId))
                    {
                        continue;
                    }

                    bool issued = _issued.ContainsKey(page.Page.PageId);
                    bool conflicted = _conflictedPages.Contains(page.Page.PageId);
                    _settlements[page.Page.PageId] = new OcrPageSettlement(page.Page,
                        issued && !cancelled ? OcrPageSettlementState.Failed : OcrPageSettlementState.Cancelled,
                        conflicted ? OcrSettlementCodes.SourceRevisionConflict
                        : issued && !cancelled ? AppErrorCodes.InvalidState : null,
                        conflicted
                            ? "The page's committed revision changed while the job was running."
                            : issued && !cancelled
                                ? "The page was issued but the job ended before it was settled."
                                : null,
                        FailedInputCount: issued ? page.Inputs.Count : 0);
                }

                leaked = _outstandingImages.Values.ToArray();
                _outstandingImages.Clear();
                if (_activeReads == 0)
                {
                    _readsDrained.TrySetResult();
                }
            }

            // Wakes budget waiters and cooperative loaders linked to the job's read lifetime;
            // the drain below still waits for any loader that ignores it. Cancellation and
            // lease-release callbacks can throw, so failures are collected and rethrown only
            // after every registered read has drained — nothing may strand the drain.
            List<Exception> failures = [];
            try
            {
                _readLifetime.Cancel();
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }

            foreach (OcrInputImage image in leaked)
            {
                try
                {
                    image.Dispose();
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
            }

            await _readsDrained.Task.ConfigureAwait(false);
            // No registered read exists past this point, so nothing still touches the lifetime.
            _readLifetime.Dispose();
            if (failures.Count > 0)
            {
                throw new AggregateException("OCR job read lifetime or image releases failed.",
                    failures);
            }
        }

        private void ReleaseImage(Guid leaseId)
        {
            try
            {
                lock (_gate)
                {
                    _outstandingImages.Remove(leaseId);
                }
            }
            finally
            {
                _imageBudget.Release();
            }
        }

        // Ownership moves into a release object, rather than a closure over a local that is
        // also disposed on construction failure or modified after the delegate is created.
        private sealed class ImageLeaseRelease(HostOcrJob owner, Guid leaseId, OcrInputImage image)
        {
            private OcrInputImage? _image = image;

            public void Release()
            {
                OcrInputImage? owned = Interlocked.Exchange(ref _image, null);
                if (owned is null)
                {
                    return;
                }

                try
                {
                    owned.Dispose();
                }
                finally
                {
                    owner.ReleaseImage(leaseId);
                }
            }
        }

        /// <summary>
        /// Running-job plus issued-and-unsettled checks shared by every stage of a read. Must be
        /// called under <see cref="_gate"/>.
        /// </summary>
        private void EnsureUsable(OcrJobInput input)
        {
            EnsureRunning();
            if (!_issuedInputs.TryGetValue(input.Id, out OcrJobInput? issued) ||
                !ReferenceEquals(issued, input))
            {
                throw new ArgumentException("The OCR input was not issued by this job.", nameof(input));
            }

            if (_settlements.ContainsKey(_inputPages[input.Id]))
            {
                throw new InvalidOperationException(
                    "The OCR input's page is already settled.");
            }
        }

        private OcrSubmissionReceipt? RejectUnlessIssued(ExtensionPageIdentity page)
        {
            if (page is null || !_issued.TryGetValue(page.PageId, out OcrJobPage? issued))
            {
                return OcrSubmissionReceipt.Rejected(AppErrorCodes.InvalidArgument,
                    "OCR submissions require a page issued by this job.");
            }

            if (issued.Page != page)
            {
                return OcrSubmissionReceipt.Rejected(AppErrorCodes.InvalidArgument,
                    "OCR submissions belong to a different page or source revision.");
            }

            if (_settlements.ContainsKey(page.PageId))
            {
                return OcrSubmissionReceipt.Rejected(AppErrorCodes.InvalidArgument,
                    "The page was already settled; duplicate submissions are invalid.");
            }

            if (_conflictedPages.Contains(page.PageId))
            {
                // A terminal conflict is not erasable: further submissions under the stale
                // identity must not slip an image result onto the newer source revision.
                return OcrSubmissionReceipt.Rejected(OcrSettlementCodes.SourceRevisionConflict,
                    "The page's committed revision changed; this issued page is closed.");
            }

            return null;
        }

        private void EnsureRunning()
        {
            if (!_running)
            {
                throw new InvalidOperationException("The OCR job is no longer running.");
            }
        }
    }
}
