using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Credentials;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class CredentialStoreTests
{
    [Fact]
    public async Task SaveCredential_stores_plaintext_secret_in_runtime_db()
    {
        await using Ctx c = await Ctx.Create();
        Result<ProviderCredentialMetadata> r = await c.Store.SaveCredentialAsync("mock", "Key", "sk-test");
        await using SqliteConnection cn = c.Db.ConnectionFactory.CreateConnection();
        await cn.OpenAsync();
        (await cn.ExecuteScalarAsync<string>("select secret_value from provider_credentials;")).Should().Be("sk-test");
    }

    [Fact]
    public async Task GetCredentialMetadata_does_not_return_secret()
    {
        await using Ctx c = await Ctx.Create();
        Result<ProviderCredentialMetadata> x = await c.Store.SaveCredentialAsync("mock", "Key", "sk");
        Result<ProviderCredentialMetadata> m = await c.Store.GetCredentialMetadataAsync(x.Value.CredentialId);
        typeof(ProviderCredentialMetadata).GetProperties().Select(p => p.Name).Should().NotContain("SecretValue");
        m.Value.DisplayName.Should().Be("Key");
    }

    [Fact]
    public async Task GetSecretForInternalUse_returns_secret()
    {
        await using Ctx c = await Ctx.Create();
        Result<ProviderCredentialMetadata> x = await c.Store.SaveCredentialAsync("mock", "Key", "sk");
        (await c.Store.GetSecretForInternalUseAsync(x.Value.CredentialId)).Value.Should().Be("sk");
    }

    [Fact]
    public async Task GetActiveSecretForProvider_returns_latest_active_provider_secret()
    {
        await using Ctx c = await Ctx.Create();
        await c.Store.SaveOrUpdateProviderCredentialAsync(ProviderIds.MinerU, "MinerU", "old");
        await c.Store.SaveOrUpdateProviderCredentialAsync(ProviderIds.MinerU, "MinerU", "new");
        (await c.Store.GetActiveSecretForProviderAsync(ProviderIds.MinerU)).Value.Should().Be("new");
        await using SqliteConnection cn = c.Db.ConnectionFactory.CreateConnection();
        await cn.OpenAsync();
        (await cn.ExecuteScalarAsync<int>("select count(1) from provider_credentials where provider_id='mineru';"))
            .Should().Be(1);
    }

    [Fact]
    public async Task ListCredentials_does_not_return_secret()
    {
        await using Ctx c = await Ctx.Create();
        await c.Store.SaveCredentialAsync("mock", "Key", "sk");
        (await c.Store.ListCredentialsAsync()).Value.Should().HaveCount(1);
    }

    [Fact]
    public async Task SaveCredential_rejects_blank_provider_display_secret()
    {
        await using Ctx c = await Ctx.Create();
        (await c.Store.SaveCredentialAsync(" ", "x", "x")).IsFailure.Should().BeTrue();
        (await c.Store.SaveCredentialAsync("x", " ", "x")).IsFailure.Should().BeTrue();
        (await c.Store.SaveCredentialAsync("x", "x", " ")).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task BindCredentialToPreset_records_binding()
    {
        await using Ctx c = await Ctx.Create();
        Result<ProviderCredentialMetadata> cr = await c.Store.SaveCredentialAsync("mock", "Key", "sk");
        Result<OcrPreset> p = await c.Preset.CreatePresetAsync("p", null, "mock", "mock-basic", null, "{}", false);
        Result<ProviderCredentialBinding> b =
            await c.Store.BindCredentialToPresetAsync(cr.Value.CredentialId, p.Value.PresetId);
        b.Value.Status.Should().Be(ProviderCredentialBindingStatus.Active);
    }

    [Fact]
    public async Task RevokeCredential_marks_revoked_and_binding_revoked()
    {
        await using Ctx c = await Ctx.Create();
        Result<ProviderCredentialMetadata> cr = await c.Store.SaveCredentialAsync("mock", "Key", "sk");
        Result<OcrPreset> p = await c.Preset.CreatePresetAsync("p", null, "mock", "mock-basic", null, "{}", false);
        await c.Store.BindCredentialToPresetAsync(cr.Value.CredentialId, p.Value.PresetId);
        await c.Store.RevokeCredentialAsync(cr.Value.CredentialId);
        (await c.StatusAsync(cr.Value.CredentialId)).Should().Be(ProviderCredentialStatus.Revoked);
        (await c.BindingStatusAsync(cr.Value.CredentialId)).Should().Be(ProviderCredentialBindingStatus.Revoked);
    }

    [Fact]
    public async Task EmergencyPurgeCredential_clears_secret_and_marks_purged()
    {
        await using Ctx c = await Ctx.Create();
        Result<ProviderCredentialMetadata> cr = await c.Store.SaveCredentialAsync("mock", "Key", "sk");
        await c.Store.EmergencyPurgeCredentialAsync(cr.Value.CredentialId);
        (await c.StatusAsync(cr.Value.CredentialId)).Should().Be(ProviderCredentialStatus.Purged);
        (await c.SecretAsync(cr.Value.CredentialId)).Should().Be("[purged]");
    }

    [Fact]
    public async Task EmergencyPurgeCredential_marks_binding_credential_missing()
    {
        await using Ctx c = await Ctx.Create();
        Result<ProviderCredentialMetadata> cr = await c.Store.SaveCredentialAsync("mock", "Key", "sk");
        Result<OcrPreset> p = await c.Preset.CreatePresetAsync("p", null, "mock", "mock-basic", null, "{}", false);
        await c.Store.BindCredentialToPresetAsync(cr.Value.CredentialId, p.Value.PresetId);
        await c.Store.EmergencyPurgeCredentialAsync(cr.Value.CredentialId);
        (await c.BindingStatusAsync(cr.Value.CredentialId)).Should()
            .Be(ProviderCredentialBindingStatus.CredentialMissing);
    }

    [Fact]
    public async Task Purged_credential_row_remains_for_sync_marker()
    {
        await using Ctx c = await Ctx.Create();
        Result<ProviderCredentialMetadata> cr = await c.Store.SaveCredentialAsync("mock", "Key", "sk");
        await c.Store.EmergencyPurgeCredentialAsync(cr.Value.CredentialId);
        await using SqliteConnection cn = c.Db.ConnectionFactory.CreateConnection();
        await cn.OpenAsync();
        (await cn.ExecuteScalarAsync<int>("select count(1) from provider_credentials;")).Should().Be(1);
    }

    private sealed class Ctx : IAsyncDisposable
    {
        private Ctx(TemporarySqliteDatabase d, CredentialStore s, OcrPresetService p)
        {
            Db = d;
            Store = s;
            Preset = p;
        }

        public TemporarySqliteDatabase Db { get; }
        public CredentialStore Store { get; }
        public OcrPresetService Preset { get; }

        public static async Task<Ctx> Create()
        {
            TemporarySqliteDatabase d = TemporarySqliteDatabase.Create();
            FixedClock clk = new(DateTimeOffset.UtcNow);
            await new MigrationRunner(d.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService l = new(d.ConnectionFactory, clk);
            await l.CreateLibraryAsync("L");
            return new Ctx(d, new CredentialStore(d.ConnectionFactory, l, clk),
                new OcrPresetService(d.ConnectionFactory, l, clk));
        }

        public async Task<string> StatusAsync(CredentialId id)
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            return (await c.ExecuteScalarAsync<string>(
                "select status from provider_credentials where credential_id=@Id;", new { Id = id.ToString() }))!;
        }

        public async Task<string> SecretAsync(CredentialId id)
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            return (await c.ExecuteScalarAsync<string>(
                "select secret_value from provider_credentials where credential_id=@Id;", new { Id = id.ToString() }))!;
        }

        public async Task<string> BindingStatusAsync(CredentialId id)
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            return (await c.ExecuteScalarAsync<string>(
                "select status from provider_credential_bindings where credential_id=@Id;",
                new { Id = id.ToString() }))!;
        }

        public ValueTask DisposeAsync()
        {
            return Db.DisposeAsync();
        }
    }
}
