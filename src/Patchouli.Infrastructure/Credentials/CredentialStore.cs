using System.Text.Json;
using System.Text.Json.Nodes;
using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;

namespace Patchouli.Infrastructure.Credentials;

public sealed class CredentialStore : ICredentialStore
{
    private readonly string _path;

    public CredentialStore(string path)
    {
        _path = path;
    }

    public async Task<Result<ProviderCredentialMetadata>> SaveAsync(string providerId, string displayName,
        string secretValue, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(secretValue))
        {
            return Result<ProviderCredentialMetadata>.Failure(AppErrorCodes.ValidationFailed,
                "Provider and secret are required.");
        }

        SemaphoreSlim gate = SettingsFileWriteCoordinator.ForPath(_path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            JsonObject root = await ReadRootAsync(cancellationToken);
            JsonObject credentials = GetObject(root, "Credentials");
            JsonArray providers = GetArray(credentials, "Providers");
            string provider = providerId.Trim().ToLowerInvariant();
            JsonObject? existing = providers.OfType<JsonObject>().FirstOrDefault(item =>
                string.Equals(item["ProviderId"]?.GetValue<string>(), provider, StringComparison.OrdinalIgnoreCase));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string id = existing?["CredentialId"]?.GetValue<string>() ?? Guid.NewGuid().ToString("D");
            JsonObject value = new()
            {
                ["CredentialId"] = id,
                ["ProviderId"] = provider,
                ["DisplayName"] = string.IsNullOrWhiteSpace(displayName) ? provider : displayName.Trim(),
                ["SecretValue"] = secretValue.Trim(),
                ["Status"] = ProviderCredentialStatus.Active,
                ["CreatedAt"] = existing?["CreatedAt"]?.GetValue<string>() ?? now.ToString("O"),
                ["UpdatedAt"] = now.ToString("O")
            };
            for (int index = providers.Count - 1; index >= 0; index--)
            {
                if (providers[index] is JsonObject item &&
                    string.Equals(item["ProviderId"]?.GetValue<string>(), provider, StringComparison.OrdinalIgnoreCase))
                {
                    providers.RemoveAt(index);
                }
            }

            providers.Add(value);
            bool saved = await WriteRootAsync(root, cancellationToken);
            return saved
                ? Result<ProviderCredentialMetadata>.Success(ToMetadata(value))
                : Result<ProviderCredentialMetadata>.Failure(AppErrorCodes.DatabaseError,
                    "Credential settings could not be saved.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Result<string>> GetActiveSecretForProviderAsync(string providerId,
        CancellationToken cancellationToken = default)
    {
        JsonObject root = await ReadRootAsync(cancellationToken);
        JsonArray providers = GetArray(GetObject(root, "Credentials"), "Providers");
        JsonObject? value = providers.OfType<JsonObject>().FirstOrDefault(item =>
            string.Equals(item["ProviderId"]?.GetValue<string>(), providerId.Trim(),
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item["Status"]?.GetValue<string>(), ProviderCredentialStatus.Active,
                StringComparison.OrdinalIgnoreCase));
        string? secret = value?["SecretValue"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(secret)
            ? Result<string>.Failure(AppErrorCodes.NotFound, "Credential was not found.")
            : Result<string>.Success(secret);
    }

    public async Task<Result> RemoveAsync(string providerId, CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate = SettingsFileWriteCoordinator.ForPath(_path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            JsonObject root = await ReadRootAsync(cancellationToken);
            JsonArray providers = GetArray(GetObject(root, "Credentials"), "Providers");
            for (int index = providers.Count - 1; index >= 0; index--)
            {
                if (providers[index] is JsonObject item &&
                    string.Equals(item["ProviderId"]?.GetValue<string>(), providerId.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    providers.RemoveAt(index);
                }
            }

            bool saved = await WriteRootAsync(root, cancellationToken);
            return saved
                ? Result.Success()
                : Result.Failure(AppErrorCodes.DatabaseError, "Credential settings could not be saved.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Result<IReadOnlyList<ProviderCredentialMetadata>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        JsonObject root = await ReadRootAsync(cancellationToken);
        JsonArray providers = GetArray(GetObject(root, "Credentials"), "Providers");
        return Result<IReadOnlyList<ProviderCredentialMetadata>>.Success(
            providers.OfType<JsonObject>().Select(ToMetadata).ToArray());
    }

    private async Task<JsonObject> ReadRootAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new JsonObject();
        }

        await using FileStream stream = File.OpenRead(_path);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject ??
               new JsonObject();
    }

    private async Task<bool> WriteRootAsync(JsonObject root, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temporaryPath, _path, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static JsonObject GetObject(JsonObject root, string name)
    {
        if (root[name] is not JsonObject value)
        {
            value = new JsonObject();
            root[name] = value;
        }

        return value;
    }

    private static JsonArray GetArray(JsonObject root, string name)
    {
        if (root[name] is not JsonArray value)
        {
            value = new JsonArray();
            root[name] = value;
        }

        return value;
    }

    private static ProviderCredentialMetadata ToMetadata(JsonObject value)
    {
        return new ProviderCredentialMetadata(CredentialId.Parse(value["CredentialId"]!.GetValue<string>()),
            value["ProviderId"]!.GetValue<string>(), value["DisplayName"]!.GetValue<string>(),
            value["Status"]!.GetValue<string>(), DateTimeOffset.Parse(value["CreatedAt"]!.GetValue<string>()),
            DateTimeOffset.Parse(value["UpdatedAt"]!.GetValue<string>()));
    }
}
