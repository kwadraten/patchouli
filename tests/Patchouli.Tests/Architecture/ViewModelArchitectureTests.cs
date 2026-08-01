using System.Reflection;
using System.Text.RegularExpressions;
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
        Assembly uiAssembly = typeof(MainWindowViewModel).Assembly;

        List<Type> viewModelTypes = uiAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("ViewModel"))
            .ToList();

        // Act & Assert
        List<string> invalidTypes = new();
        foreach (Type type in viewModelTypes)
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
        string uiRoot = TestPaths.FromRepositoryRoot("src", "Patchouli.UI");
        string[] violations = Directory.EnumerateFiles(uiRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(entry =>
                entry.line.Contains("Dispatcher.UIThread.Post(async", StringComparison.Ordinal) ||
                Regex.IsMatch(entry.line, @"\b_\s*=\s*Task\.Run\s*\("))
            .Select(entry => $"{Path.GetRelativePath(uiRoot, entry.path)}:{entry.number}: {entry.line.Trim()}")
            .ToArray();

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Infrastructure_broad_catches_must_report_or_classify_exceptions()
    {
        string root = TestPaths.FromRepositoryRoot("src", "Patchouli.Infrastructure");
        Regex broadCatch = new(@"catch\s*\(Exception\s+\w+\)");
        string[] violations = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(entry => broadCatch.IsMatch(entry.line) &&
                            !(entry.line.Contains("UnexpectedExceptionReporter.ReportCatch", StringComparison.Ordinal) ||
                              entry.line.Contains("// Reported below", StringComparison.Ordinal) ||
                              entry.line.Contains("exception is IOException or UnauthorizedAccessException",
                                  StringComparison.Ordinal) ||
                              entry.line.Contains("exception is JsonException or InvalidOperationException",
                                  StringComparison.Ordinal) ||
                              entry.line.Contains("exception is XmlException or InvalidOperationException",
                                  StringComparison.Ordinal)))
            .Select(entry => $"{Path.GetRelativePath(root, entry.path)}:{entry.number}: {entry.line.Trim()}")
            .ToArray();

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Business_view_models_must_use_the_library_setting_coordinator()
    {
        string viewModelRoot = TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "ViewModels");
        string[] violations = Directory.EnumerateFiles(viewModelRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(entry =>
                entry.line.Contains("LibrarySettings.GetAsync", StringComparison.Ordinal) ||
                entry.line.Contains("LibrarySettings.SaveAsync", StringComparison.Ordinal) ||
                entry.line.Contains("LibrarySettings.DeleteAsync", StringComparison.Ordinal))
            .Select(entry => $"{Path.GetRelativePath(viewModelRoot, entry.path)}:{entry.number}: {entry.line.Trim()}")
            .ToArray();

        violations.Should().BeEmpty();
    }
}
