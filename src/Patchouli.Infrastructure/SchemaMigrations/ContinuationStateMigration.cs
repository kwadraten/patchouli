using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Infrastructure.Documents;

namespace Patchouli.Infrastructure.Migrations;

/// <summary>
/// Issue #29 的旧接续数据迁移（迁移 054 的数据步，与 048 的表示迁移同一模式）：
/// 在同一事务中给 <c>document_boxes</c> 增加 <c>continuation_state</c> 列，并把既有
/// 空框推断产生的接续分类。恢复判据只有一条硬证据——本框 payload 是否已有自己的
/// 文字：有文字说明文字未被合并走，接续按显式关系保留；无文字则全文仍在源框、
/// 本框文字无法从库内数据恢复（原始 MinerU 产物不随库保留，库中不存在
/// preproc_blocks 或任何等价的每框原文），标记 <c>legacy_unrecovered</c> 并逐框
/// 写入诊断，等待人工在工作台重新分配文字。绝不按字符数或几何比例猜拆，
/// 绝不把全文复制到每个接续框，也不取消关系或删除空框。框 ID、页 ID、几何、
/// 阅读顺序与历史修订一律不动；已提交修订的不可变触发器按 048 的方式在事务内
/// 临时摘下并恢复，失败时由 MigrationRunner 整体回滚，不留半迁移数据。
/// 本步可重入：分类是确定性的，诊断先删本迁移旧行再写。
/// </summary>
internal static class ContinuationStateMigration
{
    public static async Task ApplyAsync(SqliteConnection connection, DbTransaction transaction, string sql)
    {
        string guard = await connection.ExecuteScalarAsync<string>(
                           "select sql from sqlite_master where type='trigger' and name='document_boxes_committed_update_guard';",
                           transaction: transaction) ??
                       throw new InvalidOperationException("The committed-box update guard is missing.");
        // Representation migration is the only exception to committed-box immutability.
        // Both the guard and every row change are restored by the runner's rollback on failure.
        await connection.ExecuteAsync("drop trigger document_boxes_committed_update_guard;",
            transaction: transaction);
        await connection.ExecuteAsync(sql, transaction: transaction);
        await ClassifyLegacyContinuationsAsync(connection, transaction);
        await connection.ExecuteAsync(guard, transaction: transaction);
    }

    private static async Task ClassifyLegacyContinuationsAsync(
        SqliteConnection connection,
        DbTransaction transaction)
    {
        await connection.ExecuteAsync(
            "delete from continuation_migration_diagnostics where migration_id = '054';",
            transaction: transaction);

        LinkedBoxRow[] linked = (await connection.QueryAsync<LinkedBoxRow>(
            """
            select box_id as BoxId, tree_revision_id as TreeRevisionId, box_type as BoxType,
                sub_type as SubType, payload_json as PayloadJson
            from document_boxes
            where continues_from_box_id is not null;
            """,
            transaction: transaction)).ToArray();

        string now = DateTimeOffset.UtcNow.ToString("O");
        foreach (LinkedBoxRow row in linked)
        {
            DocumentBoxPayload? payload =
                DocumentBoxPayloadSerializer.Deserialize(row.BoxType, row.SubType, row.PayloadJson);
            bool hasOwnText = HasOwnText(payload);
            string action;
            string detail;
            if (hasOwnText)
            {
                action = "kept_explicit";
                detail = "本框已有自己的文字，接续关系按显式关系保留，未标记兼容状态。";
            }
            else
            {
                await connection.ExecuteAsync(
                    """
                    update document_boxes set continuation_state = 'legacy_unrecovered'
                    where tree_revision_id = @TreeRevisionId and box_id = @BoxId;
                    """,
                    new { row.TreeRevisionId, row.BoxId },
                    transaction);
                action = "marked_legacy_unrecovered";
                detail = "接续由旧版空框推断产生；原始 MinerU 产物未随库保留，库内无每框原文可恢复，" +
                         "全文保留在源框，本框显式标记为未恢复，等待人工分配文字。";
            }

            await connection.ExecuteAsync(
                """
                insert into continuation_migration_diagnostics
                    (migration_id, box_id, tree_revision_id, action, detail, created_at)
                values ('054', @BoxId, @TreeRevisionId, @Action, @Detail, @CreatedAt);
                """,
                new { row.BoxId, row.TreeRevisionId, Action = action, Detail = detail, CreatedAt = now },
                transaction);
        }
    }

    private static bool HasOwnText(DocumentBoxPayload? payload)
    {
        return payload switch
        {
            TextBoxPayload value => !string.IsNullOrWhiteSpace(value.Markdown),
            ListBoxPayload value => !string.IsNullOrWhiteSpace(value.Markdown),
            AlgorithmBoxPayload value => !string.IsNullOrWhiteSpace(value.Markdown),
            TableBoxPayload value => !string.IsNullOrWhiteSpace(value.Markdown) ||
                                     !string.IsNullOrWhiteSpace(value.Html),
            CodeBoxPayload value => !string.IsNullOrWhiteSpace(value.Code),
            EquationBoxPayload value => !string.IsNullOrWhiteSpace(value.Latex),
            MediaBoxPayload value => !string.IsNullOrWhiteSpace(value.Description),
            _ => false
        };
    }

    private sealed class LinkedBoxRow
    {
        public string BoxId { get; set; } = string.Empty;
        public string TreeRevisionId { get; set; } = string.Empty;
        public string BoxType { get; set; } = string.Empty;
        public string? SubType { get; set; }
        public string? PayloadJson { get; set; }
    }
}
