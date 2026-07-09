using System.Threading.Tasks;

namespace Patchouli.UI.Services;

public interface IDialogService
{
    Task ShowDialogAsync(object viewModel);
    Task<TResult?> ShowDialogAsync<TResult>(object viewModel);
}
