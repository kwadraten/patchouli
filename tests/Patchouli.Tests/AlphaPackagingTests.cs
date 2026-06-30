using FluentAssertions;
using Patchouli.Core;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class AlphaPackagingTests
{
    [Fact] public void BuildInfo_exposes_alpha_version() { BuildInfo.AppName.Should().Be("Patchouli"); BuildInfo.Version.Should().Be("0.1.0-alpha-rc1"); BuildInfo.SchemaVersion.Should().Be(AppSchemaVersion.Current); }
    [Fact] public void AppRuntimeOptions_defaults_do_not_place_db_in_sync_root() { var o=AppRuntimeOptions.FromEnvironment(); Path.GetFullPath(o.RuntimeDatabasePath).Should().NotStartWith(Path.GetFullPath(o.DefaultSyncRoot)); o.UseMockOcrOnly.Should().BeTrue(); }
    [Fact] public void Logger_redacts_secret_values() { SimpleFileLogger.Redact("secret_value=sk-test-secret").Should().NotContain("sk-test-secret"); }
    [Fact] public void Logger_does_not_write_provider_secret() { SimpleFileLogger.Redact("api_key: provider-secret-value").Should().NotContain("provider-secret-value"); }
    [Fact] public void Alpha_smoke_script_exists() { File.Exists(TestPaths.FromRepositoryRoot("scripts","alpha-smoke-test.sh")).Should().BeTrue(); }
    [Fact] public void Agent_docs_live_under_agent_directory() { File.Exists(TestPaths.FromRepositoryRoot(".agent","PRD.md")).Should().BeTrue(); File.Exists(TestPaths.FromRepositoryRoot(".agent","domain.md")).Should().BeTrue(); File.Exists(TestPaths.FromRepositoryRoot(".agent","minimal-closed-loop-execution-plan.md")).Should().BeTrue(); Directory.Exists(TestPaths.FromRepositoryRoot("docs")).Should().BeFalse(); }
    [Fact] public void Agent_docs_describe_mineru_closed_loop() { var plan=File.ReadAllText(TestPaths.FromRepositoryRoot(".agent","minimal-closed-loop-execution-plan.md")); plan.Should().Contain("MinerU").And.Contain("MCP remains read-only and text-only"); }
    [Fact] public void UI_version_info_viewmodel_exposes_version_schema_db_path() { var vm=new MainWindowViewModel(new TestClipboard()); vm.VersionInfo.Should().Contain("0.1.0-alpha").And.Contain("Schema").And.Contain(vm.RuntimeDatabasePath); }
    [Fact] public void PRD_documents_queue_search_and_mcp_boundaries() { var r=File.ReadAllText(TestPaths.FromRepositoryRoot(".agent","PRD.md")); r.Should().Contain("MCP never triggers OCR or index rebuild").And.Contain("Search Profiles").And.Contain("FTS index is a rebuildable local cache"); }
    [Fact] public void BuildInfo_exposes_alpha_rc_version() { BuildInfo.Version.Should().Be("0.1.0-alpha-rc1"); }
    [Fact] public void Alpha_rc_script_exists_without_docs_dependency() { var script=File.ReadAllText(TestPaths.FromRepositoryRoot("scripts","alpha-rc-smoke-test.sh")); script.Should().Contain("dotnet restore").And.NotContain("docs/"); }
    [Fact] public void ADR_records_mineru_as_product_ocr_provider() { var adr=File.ReadAllText(TestPaths.FromRepositoryRoot(".agent","adr","0014-use-mineru-as-first-product-ocr-provider.md")); adr.Should().Contain("MinerU").And.Contain("first product OCR/layout path"); }
    private sealed class TestClipboard : IClipboardService { public Task SetTextAsync(string text)=>Task.CompletedTask; }
}
