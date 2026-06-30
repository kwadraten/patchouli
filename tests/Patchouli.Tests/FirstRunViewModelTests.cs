using FluentAssertions;
using Patchouli.Core.Import;
using Patchouli.Infrastructure.Workflows;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class FirstRunViewModelTests
{
    [Fact]
    public async Task OpenDatabaseCommand_moves_from_database_to_library_step()
    {
        var openedPath = "";
        var viewModel = new FirstRunViewModel(path =>
        {
            openedPath = path;
            return Task.FromResult<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)>((null!, new PdfDiscoveryService()));
        })
        {
            DatabasePath = @"C:\temp\runtime.sqlite"
        };

        await viewModel.OpenDatabaseCommand.ExecuteAsync();

        openedPath.Should().Be(@"C:\temp\runtime.sqlite");
        viewModel.CurrentStep.Should().Be("library");
        viewModel.ShowLibraryStep.Should().BeTrue();
        viewModel.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task CreateLibrary_without_open_database_stays_recoverable()
    {
        var viewModel = new FirstRunViewModel(_ =>
            Task.FromResult<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)>((null!, new PdfDiscoveryService())));

        await viewModel.CreateLibraryCommand.ExecuteAsync();

        viewModel.CurrentStep.Should().Be("database");
        viewModel.HasError.Should().BeTrue();
        viewModel.LastError.Should().Contain("database");
    }

    [Fact]
    public async Task RunMinerUCommand_without_token_prompts_for_token()
    {
        var viewModel = new FirstRunViewModel(_ =>
            Task.FromResult<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)>((null!, new PdfDiscoveryService())));
        SetState(viewModel, new FirstRunWorkflowState(
            FirstRunStep.MinerUConfig,
            "Configure MinerU token for OCR extraction.",
            "input.pdf",
            null,
            null,
            null,
            Guid.NewGuid().ToString(),
            null,
            false));

        await viewModel.RunMinerUCommand.ExecuteAsync();

        viewModel.CurrentStep.Should().Be(FirstRunStep.MinerUConfig);
        viewModel.HasError.Should().BeTrue();
        viewModel.LastError.Should().Contain("MinerU API token");
    }

    [Fact]
    public async Task FinishSetupCommand_requires_token()
    {
        var viewModel = new FirstRunViewModel(_ =>
            Task.FromResult<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)>((null!, new PdfDiscoveryService())));
        SetState(viewModel, new FirstRunWorkflowState(
            FirstRunStep.MinerUConfig,
            "Configure MinerU OCR.",
            "input.pdf",
            null,
            null,
            null,
            Guid.NewGuid().ToString(),
            null,
            false));

        await viewModel.FinishSetupCommand.ExecuteAsync();

        viewModel.CurrentStep.Should().Be(FirstRunStep.MinerUConfig);
        viewModel.HasError.Should().BeTrue();
        viewModel.LastError.Should().Contain("MinerU API token");
    }

    [Fact]
    public async Task FinishSetupCommand_completes_after_token()
    {
        var viewModel = new FirstRunViewModel(_ =>
            Task.FromResult<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)>((null!, new PdfDiscoveryService())))
        {
            MinerUToken = "token"
        };
        SetState(viewModel, new FirstRunWorkflowState(
            FirstRunStep.MinerUConfig,
            "Configure MinerU OCR.",
            "input.pdf",
            null,
            null,
            null,
            Guid.NewGuid().ToString(),
            null,
            false));

        await viewModel.FinishSetupCommand.ExecuteAsync();

        viewModel.CurrentStep.Should().Be(FirstRunStep.Complete);
        viewModel.IsComplete.Should().BeTrue();
    }

    private static void SetState(FirstRunViewModel viewModel, FirstRunWorkflowState state)
    {
        var field = typeof(FirstRunViewModel).GetField("_state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field!.SetValue(viewModel, state);
    }
}
