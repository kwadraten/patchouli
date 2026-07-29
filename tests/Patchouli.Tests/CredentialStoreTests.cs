using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Credentials;
using Patchouli.Infrastructure.Mcp;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class CredentialStoreTests
{
    [Fact]
    public async Task Save_round_trips_provider_secret_in_json()
    {
        await using Context context = new();
        Result<ProviderCredentialMetadata> saved = await context.Store.SaveAsync("mineru", "MinerU", "token");
        saved.IsSuccess.Should().BeTrue();
        (await context.Store.GetActiveSecretForProviderAsync("mineru")).Value.Should().Be("token");
        File.ReadAllText(context.Path).Should().Contain("\"Credentials\"").And.Contain("token");
    }

    [Fact]
    public async Task Save_replaces_existing_provider_without_duplicates()
    {
        await using Context context = new();
        await context.Store.SaveAsync("mineru", "MinerU", "old");
        await context.Store.SaveAsync("mineru", "MinerU", "new");
        (await context.Store.GetActiveSecretForProviderAsync("mineru")).Value.Should().Be("new");
        (await context.Store.ListAsync()).Value.Should().ContainSingle();
    }

    [Fact]
    public async Task Remove_deletes_secret_without_affecting_mcp_token()
    {
        await using Context context = new("""{"Mcp":{"ServerToken":"mcp-token"}}""");
        await context.Store.SaveAsync("mineru", "MinerU", "provider-token");
        (await context.Store.RemoveAsync("mineru")).IsSuccess.Should().BeTrue();
        (await context.Store.GetActiveSecretForProviderAsync("mineru")).IsFailure.Should().BeTrue();
        File.ReadAllText(context.Path).Should().Contain("mcp-token").And.NotContain("provider-token");
    }

    [Fact]
    public async Task Metadata_does_not_expose_secret()
    {
        await using Context context = new();
        await context.Store.SaveAsync("mineru", "MinerU", "secret");
        typeof(ProviderCredentialMetadata).GetProperties().Select(value => value.Name)
            .Should().NotContain("SecretValue");
    }

    [Fact]
    public async Task Credential_and_mcp_saves_share_the_same_settings_file_writer()
    {
        await using Context context = new();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        McpServerSettingsService mcp = new(context.Path, clock);

        Task<Result<McpServerSettings>> mcpSave = mcp.SaveSettingsAsync(
            McpServerSettingsService.DefaultSettings(clock.UtcNow) with { Port = 4542 });
        Task<Result<ProviderCredentialMetadata>> credentialSave =
            context.Store.SaveAsync("mineru", "MinerU", "token");

        await Task.WhenAll(mcpSave, credentialSave);
        Result<McpServerSettings> mcpResult = await mcpSave;
        Result<ProviderCredentialMetadata> credentialResult = await credentialSave;
        PatchouliAppSettings settings = PatchouliAppSettings.Load(context.Path);

        mcpResult.IsSuccess.Should().BeTrue();
        credentialResult.IsSuccess.Should().BeTrue();
        settings.Mcp.Port.Should().Be(4542);
        settings.Credentials.Providers.Should().ContainSingle(provider =>
            provider.ProviderId == "mineru" && provider.SecretValue == "token");
    }

    [Fact]
    public async Task Ordinary_settings_save_preserves_credentials_owned_by_the_credential_store()
    {
        await using Context context = new();
        PatchouliAppSettings staleSettings = PatchouliAppSettings.Load(context.Path);

        (await context.Store.SaveAsync("mineru", "MinerU", "new-token")).IsSuccess.Should().BeTrue();
        (staleSettings with
        {
            Ui = staleSettings.Ui with { ShowLibraryLeftSidebar = false }
        }).Save(context.Path).IsSuccess.Should().BeTrue();

        (await context.Store.GetActiveSecretForProviderAsync("mineru")).Value.Should().Be("new-token");
    }

    [Fact]
    public async Task Ordinary_settings_save_does_not_resurrect_removed_credentials()
    {
        await using Context context = new();
        (await context.Store.SaveAsync("mineru", "MinerU", "token")).IsSuccess.Should().BeTrue();
        PatchouliAppSettings staleSettings = PatchouliAppSettings.Load(context.Path);

        (await context.Store.RemoveAsync("mineru")).IsSuccess.Should().BeTrue();
        (staleSettings with
        {
            Ui = staleSettings.Ui with { ShowLibraryRightSidebar = false }
        }).Save(context.Path).IsSuccess.Should().BeTrue();

        (await context.Store.GetActiveSecretForProviderAsync("mineru")).ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    private sealed class Context : IAsyncDisposable
    {
        public Context(string? content = null)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"patchouli-credentials-{Guid.NewGuid():N}.json");
            if (content is not null)
            {
                File.WriteAllText(Path, content);
            }

            Store = new CredentialStore(Path);
        }

        public string Path { get; }
        public CredentialStore Store { get; }

        public ValueTask DisposeAsync()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }

            return ValueTask.CompletedTask;
        }
    }
}
