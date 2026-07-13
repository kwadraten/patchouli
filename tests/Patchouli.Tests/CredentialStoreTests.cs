using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Credentials;

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
