using Avalonia.Controls;

namespace Patchouli.UI.Views;

internal static class DialogCloseBinding
{
    internal static void Bind<TViewModel, TResult>(
        Window window,
        Action<TViewModel, Action<TResult>?> setRequestClose)
        where TViewModel : class
    {
        TViewModel? attached = null;

        void Detach()
        {
            if (attached is not null)
            {
                setRequestClose(attached, null);
                attached = null;
            }
        }

        void Attach()
        {
            Detach();
            if (window.DataContext is TViewModel viewModel)
            {
                attached = viewModel;
                setRequestClose(viewModel, result => window.Close(result));
            }
        }

        window.DataContextChanged += (_, _) => Attach();
        window.Closed += (_, _) => Detach();
        Attach();
    }
}
