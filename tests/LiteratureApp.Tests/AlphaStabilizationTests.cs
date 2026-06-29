using FluentAssertions;
using LiteratureApp.UI;

namespace LiteratureApp.Tests;

public sealed class AlphaStabilizationTests
{
    [Fact]
    public void README_commands_match_existing_paths()
    {
        var readme = File.ReadAllText(TestPaths.FromRepositoryRoot("README.md"));
        readme.Should().Contain("dotnet restore LiteratureApp.sln")
            .And.Contain("dotnet build LiteratureApp.sln --no-restore")
            .And.Contain("dotnet test LiteratureApp.sln --no-build")
            .And.Contain("dotnet run --project src/LiteratureApp.UI/LiteratureApp.UI.csproj")
            .And.Contain("scripts/alpha-smoke-test.sh")
            .And.Contain("dotnet publish src/LiteratureApp.UI/LiteratureApp.UI.csproj -c Release -r osx-arm64 --self-contained false");
        File.Exists(TestPaths.FromRepositoryRoot("LiteratureApp.sln")).Should().BeTrue();
        File.Exists(TestPaths.FromRepositoryRoot("src", "LiteratureApp.UI", "LiteratureApp.UI.csproj")).Should().BeTrue();
    }

    [Fact]
    public void AlphaSmokeScript_contains_required_commands()
    {
        var script = File.ReadAllText(TestPaths.FromRepositoryRoot("scripts", "alpha-smoke-test.sh"));
        script.Should().Contain("dotnet restore LiteratureApp.sln").And.Contain("dotnet build LiteratureApp.sln --no-restore").And.Contain("dotnet test LiteratureApp.sln --no-build").And.Contain("dotnet list LiteratureApp.sln package --vulnerable --include-transitive");
    }

    [Fact]
    public void AlphaSmokeScript_uses_strict_shell_mode_and_project_root()
    {
        var script = File.ReadAllText(TestPaths.FromRepositoryRoot("scripts", "alpha-smoke-test.sh"));
        script.Should().Contain("#!/usr/bin/env bash").And.Contain("set -euo pipefail").And.Contain("cd \"${SCRIPT_DIR}/..\"");
    }

    [Theory]
    [InlineData("api_key=alpha-secret")]
    [InlineData("token: alpha-token")]
    [InlineData("provider_secret=alpha-provider-secret")]
    public void Logger_redacts_api_key_token_secret_patterns(string message)
    {
        var redacted = SimpleFileLogger.Redact(message);
        redacted.Should().Contain("[redacted]").And.NotContain("alpha-secret").And.NotContain("alpha-token").And.NotContain("alpha-provider-secret");
    }

    [Fact]
    public async Task ViewModel_action_logs_success_without_secret()
    {
        var database = Path.Combine(Path.GetTempPath(), $"literatureapp-log-{Guid.NewGuid():N}.sqlite");
        var logger = new CapturingLogger();
        try
        {
            var vm = new MainWindowViewModel(new NoopClipboard(), logger) { RuntimeDatabasePath = database };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            vm.Library.DisplayName = "Logged library";
            await vm.Library.CreateCommand.ExecuteAsync();
            logger.Messages.Should().ContainSingle(m => m.Operation == "create_library" && m.Message.Contains("Logged library"));
            logger.Messages.Select(m => m.Message).Should().NotContain(m => m.Contains("alpha-provider-secret", StringComparison.Ordinal));
        }
        finally { if (File.Exists(database)) File.Delete(database); }
    }

    [Fact]
    public async Task Logger_failure_does_not_fail_viewmodel_action()
    {
        var database = Path.Combine(Path.GetTempPath(), $"literatureapp-log-fail-{Guid.NewGuid():N}.sqlite");
        try
        {
            var vm = new MainWindowViewModel(new NoopClipboard(), new ThrowingLogger()) { RuntimeDatabasePath = database };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            vm.Library.Details.Should().NotContain("ERROR");
        }
        finally { if (File.Exists(database)) File.Delete(database); }
    }

    [Fact]
    public async Task ViewModel_validation_error_is_visible()
    {
        var database = Path.Combine(Path.GetTempPath(), $"literatureapp-validation-{Guid.NewGuid():N}.sqlite");
        try
        {
            var vm = new MainWindowViewModel(new NoopClipboard(), new CapturingLogger()) { RuntimeDatabasePath = database };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            vm.Library.DisplayName = " ";
            await vm.Library.CreateCommand.ExecuteAsync();
            vm.Library.Details.Should().Contain("validation_failed");
        }
        finally { if (File.Exists(database)) File.Delete(database); }
    }

    [Fact]
    public async Task ViewModel_missing_library_error_is_visible()
    {
        var database = Path.Combine(Path.GetTempPath(), $"literatureapp-missing-library-{Guid.NewGuid():N}.sqlite");
        try
        {
            var vm = new MainWindowViewModel(new NoopClipboard(), new CapturingLogger()) { RuntimeDatabasePath = database };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            vm.Bibliography.Title = "No library yet";
            await vm.Bibliography.CreateItemCommand.ExecuteAsync();
            vm.Bibliography.Output.Should().Contain("not_found");
        }
        finally { if (File.Exists(database)) File.Delete(database); }
    }

    [Fact]
    public void SnapshotImport_message_states_staging_only()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "LiteratureApp.UI", "ViewModels.cs")).Should().Contain("Import does not replace active runtime DB.");
    }

    [Fact]
    public void PublishOutputPath_documented_in_README()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("README.md")).Should().Contain("bin/Release/net10.0/osx-arm64/publish/");
    }

    [Fact]
    public void KnownIssuesAlpha_exists_and_lists_major_limitations()
    {
        var path = TestPaths.FromRepositoryRoot("docs", "KNOWN_ISSUES_ALPHA.md");
        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Contain("PDF rendering").And.Contain("plaintext trust model").And.Contain("MCP currently provides a read-only service layer").And.Contain("FTS is a rebuildable local cache");
    }

    [Fact]
    public void README_links_known_issues()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("README.md")).Should().Contain("docs/KNOWN_ISSUES_ALPHA.md");
    }

    private sealed class NoopClipboard : IClipboardService { public Task SetTextAsync(string text) => Task.CompletedTask; }
    private sealed class CapturingLogger : IAppLogger
    {
        public List<(string Operation, string Message)> Messages { get; } = new();
        public Task LogAsync(string operation, string message) { Messages.Add((operation, SimpleFileLogger.Redact(message))); return Task.CompletedTask; }
    }
    private sealed class ThrowingLogger : IAppLogger { public Task LogAsync(string operation, string message) => Task.FromException(new IOException("log unavailable")); }
}
