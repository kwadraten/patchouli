using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Patchouli.Core.Settings;

namespace Patchouli.UI.Services;

/// <summary>Device-local, typed, non-secret form drafts kept apart from executable settings.</summary>
internal sealed class SettingsDraftStore(string settingsPath)
{
    private readonly string _path = settingsPath + ".drafts.json";

    public async Task<T?> ReadAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        SemaphoreSlim gate = SettingsFileWriteCoordinator.ForPath(_path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            JsonObject root = await ReadRootAsync(cancellationToken);
            return root[key]?.Deserialize<T>();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task WriteAsync<T>(string key, T? draft, CancellationToken cancellationToken = default) where T : class
    {
        SemaphoreSlim gate = SettingsFileWriteCoordinator.ForPath(_path);
        await gate.WaitAsync(cancellationToken);
        string temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            JsonObject root = await ReadRootAsync(cancellationToken);
            if (draft is null)
            {
                if (!root.Remove(key))
                {
                    return;
                }
            }
            else
            {
                root[key] = JsonSerializer.SerializeToNode(draft);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            await File.WriteAllTextAsync(temporaryPath, root.ToJsonString(), cancellationToken);
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task<JsonObject> ReadRootAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new JsonObject();
        }

        await using FileStream stream = File.OpenRead(_path);
        return (await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken))?.AsObject() ??
               new JsonObject();
    }
}
