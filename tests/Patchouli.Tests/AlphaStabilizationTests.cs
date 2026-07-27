using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class AlphaStabilizationTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    private MainWindowViewModel CreateMainWindow(IClipboardService clipboard, IAppLogger logger)
    {
        return new MainWindowViewModel(clipboard, logger, settingsPath: _settings.Path);
    }

    private static MainWindowViewModel WithRuntimeDatabasePath(MainWindowViewModel viewModel, string path)
    {
        viewModel.RuntimeDatabasePath = path;
        return viewModel;
    }

    [Theory]
    [InlineData("api_key=alpha-secret")]
    [InlineData("token: alpha-token")]
    [InlineData("provider_secret=alpha-provider-secret")]
    public void Logger_redacts_api_key_token_secret_patterns(string message)
    {
        string redacted = SimpleFileLogger.Redact(message);
        redacted.Should().Contain("[redacted]").And.NotContain("alpha-secret").And.NotContain("alpha-token").And
            .NotContain("alpha-provider-secret");
    }

    [Fact]
    public async Task ViewModel_action_logs_success_without_secret()
    {
        string database = Path.Combine(Path.GetTempPath(), $"patchouli-log-{Guid.NewGuid():N}.sqlite");
        CapturingLogger logger = new();
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new NoopClipboard(), logger), database);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            vm.Library.DisplayName = "Logged library";
            await vm.Library.CreateCommand.ExecuteAsync();
            logger.Messages.Should()
                .ContainSingle(m => m.Operation == "create_library" && m.Message.Contains("Logged library"));
            logger.Messages.Select(m => m.Message).Should()
                .NotContain(m => m.Contains("alpha-provider-secret", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    [Fact]
    public async Task Logger_failure_does_not_fail_viewmodel_action()
    {
        string database = Path.Combine(Path.GetTempPath(), $"patchouli-log-fail-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(
                CreateMainWindow(new NoopClipboard(), new ThrowingLogger()), database);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            vm.Library.Details.Should().NotContain("ERROR");
        }
        finally
        {
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    [Fact]
    public async Task ViewModel_validation_error_is_visible()
    {
        string database = Path.Combine(Path.GetTempPath(), $"patchouli-validation-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(
                CreateMainWindow(new NoopClipboard(), new CapturingLogger()), database);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            vm.Library.DisplayName = " ";
            await vm.Library.CreateCommand.ExecuteAsync();
            vm.Library.Details.Should().Contain("validation_failed");
        }
        finally
        {
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    [Fact]
    public async Task ViewModel_missing_library_error_is_visible()
    {
        string database = Path.Combine(Path.GetTempPath(), $"patchouli-missing-library-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(
                CreateMainWindow(new NoopClipboard(), new CapturingLogger()), database);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            vm.Bibliography.Title = "No library yet";
            await vm.Bibliography.CreateItemCommand.ExecuteAsync();
            vm.Bibliography.Output.Should().Contain("not_found");
        }
        finally
        {
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    [Fact]
    public void SnapshotImport_message_states_staging_only()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "ViewModels.cs")).Should()
            .Contain("Import does not replace active runtime DB.");
    }

    [Fact]
    public void Runtime_options_keep_database_out_of_default_sync_root()
    {
        AppRuntimeOptions options = AppRuntimeOptions.FromAppSettings();
        Path.GetFullPath(options.RuntimeDatabasePath).Should().NotStartWith(Path.GetFullPath(options.DefaultSyncRoot));
    }

    [Fact]
    public void Agent_domain_doc_records_single_context_layout()
    {
        string path = TestPaths.FromRepositoryRoot(".agent", "domain.md");
        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Contain("single-context domain-doc layout").And.Contain(".agent/PRD.md");
    }

    private sealed class NoopClipboard : IClipboardService
    {
        public Task SetTextAsync(string text)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger : IAppLogger
    {
        public List<(string Operation, string Message)> Messages { get; } = new();

        public Task LogAsync(string operation, string message)
        {
            Messages.Add((operation, SimpleFileLogger.Redact(message)));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingLogger : IAppLogger
    {
        public Task LogAsync(string operation, string message)
        {
            return Task.FromException(new IOException("log unavailable"));
        }
    }
}
