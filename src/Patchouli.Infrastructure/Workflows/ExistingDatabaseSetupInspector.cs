using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Import;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Workflows;

/// <summary>
/// Reads an existing database's first-run prerequisites before the host opens it for use.
/// </summary>
public static class ExistingDatabaseSetupInspector
{
    public static async Task<ExistingDatabaseSetup> InspectAsync(string databasePath,
        CancellationToken cancellationToken = default)
    {
        SqliteConnectionFactory connectionFactory = new(databasePath);
        await using SqliteConnection connection = connectionFactory.CreateReadConnection();
        await connection.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(connection, "library_metadata"))
        {
            throw new InvalidOperationException("缺少 library_metadata 表。");
        }

        LibraryMetadataRow? library = await connection.QuerySingleOrDefaultAsync<LibraryMetadataRow>(
            "select library_id as LibraryId, display_name as DisplayName from library_metadata limit 1;");
        if (library is null || string.IsNullOrWhiteSpace(library.LibraryId) ||
            string.IsNullOrWhiteSpace(library.DisplayName))
        {
            throw new InvalidOperationException("缺少 library_metadata 资料库身份数据。");
        }

        bool hasSearchRoots = await CountRowsIfTableExistsAsync(connection, "file_search_root_definitions") > 0 ||
                              await CountRowsIfTableExistsAsync(connection, "file_search_roots") > 0;
        bool hasOcrPresets = await CountRowsIfTableExistsAsync(connection, "ocr_presets") > 0;

        List<string> skipped = [$"已检测到资料库「{library.DisplayName}」，跳过资料库身份步骤"];
        if (hasSearchRoots)
        {
            skipped.Add("已检测到 file_search_roots，跳过文件搜索根配置步骤");
        }

        if (hasOcrPresets)
        {
            skipped.Add("已检测到 ocr_presets，跳过 OCR Preset 配置步骤");
        }

        List<string> missing = [];
        if (!hasSearchRoots)
        {
            missing.Add("缺少 file_search_roots，请在向导中选择 PDF 扫描目录");
        }

        if (!hasOcrPresets)
        {
            missing.Add("缺少 ocr_presets，请在向导中完成 OCR Preset 配置");
        }

        string step = !hasSearchRoots
            ? FirstRunStep.Scan
            : !hasOcrPresets
                ? FirstRunStep.MinerUConfig
                : FirstRunStep.Complete;
        return new ExistingDatabaseSetup(library.LibraryId, step, string.Join("；", skipped.Concat(missing)),
            step == FirstRunStep.Complete);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        int count = await connection.ExecuteScalarAsync<int>(
            "select count(1) from sqlite_master where type = 'table' and name = @TableName;",
            new { TableName = tableName });
        return count > 0;
    }

    private static async Task<int> CountRowsIfTableExistsAsync(SqliteConnection connection, string tableName)
    {
        return await TableExistsAsync(connection, tableName)
            ? await connection.ExecuteScalarAsync<int>($"select count(1) from {tableName};")
            : 0;
    }

    private sealed class LibraryMetadataRow
    {
        public string LibraryId { get; init; } = "";
        public string DisplayName { get; init; } = "";
    }
}
