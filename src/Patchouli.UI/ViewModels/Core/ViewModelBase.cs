using System.Reactive.Disposables;
using System.Runtime.CompilerServices;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Patchouli.UI.ViewModels;

public abstract class ViewModelBase : ObservableObject, IDisposable
{
    private CompositeDisposable? _disposables;
    private int _isDisposed;

    protected void Raise([CallerMemberName] string? name = null)
    {
        OnPropertyChanged(name);
    }

    internal void Register(IDisposable disposable)
    {
        if (Volatile.Read(ref _isDisposed) == 1)
        {
            disposable.Dispose();
            return;
        }

        lock (this)
        {
            if (Volatile.Read(ref _isDisposed) == 1)
            {
                disposable.Dispose();
                return;
            }

            _disposables ??= new CompositeDisposable();
            _disposables.Add(disposable);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        if (disposing)
        {
            CompositeDisposable? toDispose;
            lock (this)
            {
                toDispose = _disposables;
                _disposables = null;
            }

            toDispose?.Dispose();
        }
    }
}
