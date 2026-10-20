namespace Patchouli.Core.Translation;

/// <summary>The four replaceable implementations required by one direct translation engine.</summary>
public sealed record TranslationProviderSet(
    ITranslationSegmenter Segmenter,
    ITranslationBatcher Batcher,
    ITranslationModelInvoker ModelInvoker,
    ITranslationResultProcessor ResultProcessor);

/// <summary>
/// Holds the replaceable translation implementations. Writes are serialized and published as one
/// immutable state, so readers always observe a consistent provider set. An instance can be filled
/// incrementally with <c>Register</c> or changed with <c>Replace</c>; <see cref="Snapshot"/> returns
/// null until all four implementations are present.
/// </summary>
public sealed class TranslationProviderRegistry
{
    private readonly object _gate = new();
    private State _state = State.Empty;

    public TranslationProviderRegistry()
    {
    }

    public TranslationProviderRegistry(
        ITranslationSegmenter segmenter,
        ITranslationBatcher batcher,
        ITranslationModelInvoker modelInvoker,
        ITranslationResultProcessor resultProcessor)
    {
        _state = new State(
            segmenter ?? throw new ArgumentNullException(nameof(segmenter)),
            batcher ?? throw new ArgumentNullException(nameof(batcher)),
            modelInvoker ?? throw new ArgumentNullException(nameof(modelInvoker)),
            resultProcessor ?? throw new ArgumentNullException(nameof(resultProcessor)));
    }

    public ITranslationSegmenter? Segmenter => Volatile.Read(ref _state).Segmenter;

    public ITranslationBatcher? Batcher => Volatile.Read(ref _state).Batcher;

    public ITranslationModelInvoker? ModelInvoker => Volatile.Read(ref _state).ModelInvoker;

    public ITranslationResultProcessor? ResultProcessor => Volatile.Read(ref _state).ResultProcessor;

    public bool IsComplete
    {
        get
        {
            State state = Volatile.Read(ref _state);
            return state.Segmenter is not null &&
                   state.Batcher is not null &&
                   state.ModelInvoker is not null &&
                   state.ResultProcessor is not null;
        }
    }

    public void Register(ITranslationSegmenter segmenter)
    {
        ArgumentNullException.ThrowIfNull(segmenter);
        lock (_gate)
        {
            if (_state.Segmenter is not null)
            {
                throw new InvalidOperationException("A translation segmenter is already registered.");
            }

            Publish(_state with { Segmenter = segmenter });
        }
    }

    public void Register(ITranslationBatcher batcher)
    {
        ArgumentNullException.ThrowIfNull(batcher);
        lock (_gate)
        {
            if (_state.Batcher is not null)
            {
                throw new InvalidOperationException("A translation batcher is already registered.");
            }

            Publish(_state with { Batcher = batcher });
        }
    }

    public void Register(ITranslationModelInvoker modelInvoker)
    {
        ArgumentNullException.ThrowIfNull(modelInvoker);
        lock (_gate)
        {
            if (_state.ModelInvoker is not null)
            {
                throw new InvalidOperationException("A translation model invoker is already registered.");
            }

            Publish(_state with { ModelInvoker = modelInvoker });
        }
    }

    public void Register(ITranslationResultProcessor resultProcessor)
    {
        ArgumentNullException.ThrowIfNull(resultProcessor);
        lock (_gate)
        {
            if (_state.ResultProcessor is not null)
            {
                throw new InvalidOperationException("A translation result processor is already registered.");
            }

            Publish(_state with { ResultProcessor = resultProcessor });
        }
    }

    public void Replace(ITranslationSegmenter segmenter)
    {
        ArgumentNullException.ThrowIfNull(segmenter);
        lock (_gate)
        {
            Publish(_state with { Segmenter = segmenter });
        }
    }

    public void Replace(ITranslationBatcher batcher)
    {
        ArgumentNullException.ThrowIfNull(batcher);
        lock (_gate)
        {
            Publish(_state with { Batcher = batcher });
        }
    }

    public void Replace(ITranslationModelInvoker modelInvoker)
    {
        ArgumentNullException.ThrowIfNull(modelInvoker);
        lock (_gate)
        {
            Publish(_state with { ModelInvoker = modelInvoker });
        }
    }

    public void Replace(ITranslationResultProcessor resultProcessor)
    {
        ArgumentNullException.ThrowIfNull(resultProcessor);
        lock (_gate)
        {
            Publish(_state with { ResultProcessor = resultProcessor });
        }
    }

    /// <summary>Returns the current provider set, or null while any implementation is missing.</summary>
    public TranslationProviderSet? Snapshot()
    {
        State state = Volatile.Read(ref _state);
        return state.Segmenter is null ||
               state.Batcher is null ||
               state.ModelInvoker is null ||
               state.ResultProcessor is null
            ? null
            : new TranslationProviderSet(
                state.Segmenter,
                state.Batcher,
                state.ModelInvoker,
                state.ResultProcessor);
    }

    private void Publish(State state)
    {
        Volatile.Write(ref _state, state);
    }

    private sealed record State(
        ITranslationSegmenter? Segmenter,
        ITranslationBatcher? Batcher,
        ITranslationModelInvoker? ModelInvoker,
        ITranslationResultProcessor? ResultProcessor)
    {
        public static State Empty { get; } = new(null, null, null, null);
    }
}
