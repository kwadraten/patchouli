using System.Reflection;
using Patchouli.UI.ViewModels;
using Xunit;
using FluentAssertions;

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

    [Fact]
    public void Ui_async_work_must_not_be_discarded_or_posted_as_async_void()
    {
        var uiRoot = TestPaths.FromRepositoryRoot("src", "Patchouli.UI");
        var violations = Directory.EnumerateFiles(uiRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(entry =>
                entry.line.Contains("Dispatcher.UIThread.Post(async", StringComparison.Ordinal) &&
                !entry.path.EndsWith("DispatcherTasks.cs", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(entry.line, @"\b_\s*=\s*Task\.Run\s*\("))
            .Select(entry => $"{Path.GetRelativePath(uiRoot, entry.path)}:{entry.number}: {entry.line.Trim()}")
            .ToArray();

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Infrastructure_broad_catches_must_report_or_classify_exceptions()
    {
        var root = TestPaths.FromRepositoryRoot("src", "Patchouli.Infrastructure");
        var broadCatch = new System.Text.RegularExpressions.Regex(@"catch\s*\(Exception\s+\w+\)");
        var violations = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(entry => broadCatch.IsMatch(entry.line) &&
                            !entry.line.Contains("UnexpectedExceptionReporter.ReportCatch", StringComparison.Ordinal) &&
                            !entry.line.Contains("exception is IOException or UnauthorizedAccessException", StringComparison.Ordinal) &&
                            !entry.line.Contains("exception is JsonException or InvalidOperationException", StringComparison.Ordinal))
            .Select(entry => $"{Path.GetRelativePath(root, entry.path)}:{entry.number}: {entry.line.Trim()}")
            .ToArray();

        violations.Should().BeEmpty();
    }
}
