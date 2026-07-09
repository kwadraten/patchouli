using System.Reflection;
using Patchouli.UI.ViewModels;
using Xunit;

namespace Patchouli.Tests.Architecture;

public class ViewModelArchitectureTests
{
    [Fact]
    public void AllViewModels_MustBeInViewModelsNamespace()
    {
        // Arrange
        var uiAssembly = typeof(MainWindowViewModel).Assembly;
        
        var viewModelTypes = uiAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("ViewModel"))
            .ToList();

        // Act & Assert
        var invalidTypes = new List<string>();
        foreach (var type in viewModelTypes)
        {
            if (type.Namespace == null || !type.Namespace.StartsWith("Patchouli.UI.ViewModels"))
            {
                invalidTypes.Add($"{type.Name} in {type.Namespace}");
            }
        }

        Assert.Empty(invalidTypes);
    }
}
