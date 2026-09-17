using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI.ViewModels.Core;

internal static class ReactiveUiFlow
{
    public static IDisposable SubscribeLatest(
        IObservable<Unit> source,
        TimeSpan throttle,
        IScheduler timingScheduler,
        IScheduler uiScheduler,
        Func<CancellationToken, Task> operation,
        Action<Exception> reportError)
    {
        // Throttle(TimeSpan.Zero) still schedules a timer callback. Skip it entirely for
        // immediate flows so their loading feedback and work begin in the same UI turn.
        IObservable<Unit> pacedSource = throttle > TimeSpan.Zero
            ? source.Throttle(throttle, timingScheduler)
            : source;

        return pacedSource
            .ObserveOn(uiScheduler)
            .Select(_ => Observable.FromAsync(cancellationToken =>
                RunSafelyAsync(operation, reportError, cancellationToken)))
            .Switch()
            .Subscribe();
    }

    public static IDisposable SubscribeBufferedSequential<T>(
        IObservable<T> source,
        TimeSpan bufferWindow,
        IScheduler timingScheduler,
        IScheduler uiScheduler,
        Func<IReadOnlyList<T>, CancellationToken, Task> operation,
        Action<Exception> reportError)
    {
        return source
            .Buffer(bufferWindow, timingScheduler)
            .Where(batch => batch.Count > 0)
            .ObserveOn(uiScheduler)
            .Select(batch => Observable.FromAsync(cancellationToken =>
                RunSafelyAsync(
                    token => operation(batch.ToArray(), token),
                    reportError,
                    cancellationToken)))
            .Concat()
            .Subscribe();
    }

    private static async Task RunSafelyAsync(
        Func<CancellationToken, Task> operation,
        Action<Exception> reportError,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // The reporter is an error-boundary callback and must never escape into Rx OnError.
            try
            {
                reportError(exception);
            }
            catch (Exception reportException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"ReactiveUiFlow reportError callback failed: {reportException}");
            }
        }
    }

    public static void BindOutput<T>(
        this IObservable<T> source,
        ViewModelBase owner,
        Action<T> setter,
        IScheduler uiScheduler,
        Action<Exception>? reportError,
        bool hasInitialValue = false,
        T? initialValue = default)
    {
        IObservable<T> stream = hasInitialValue ? source.StartWith(initialValue!) : source;

        Action<Exception> actualReportError = reportError ??
                                              (ex => UnexpectedExceptions.Sink.Report(ex, "rx-bind-output",
                                                  owner.GetType().Name));

        IDisposable subscription = stream
            .DistinctUntilChanged()
            .ObserveOn(uiScheduler)
            .Subscribe(
                value =>
                {
                    try
                    {
                        setter(value);
                    }
                    catch (Exception ex)
                    {
                        actualReportError(ex);
                    }
                },
                actualReportError);

        owner.Register(subscription);
    }
}
