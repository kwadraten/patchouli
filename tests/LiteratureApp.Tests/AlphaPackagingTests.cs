using FluentAssertions;
using LiteratureApp.Core;
using LiteratureApp.UI;

namespace LiteratureApp.Tests;

public sealed class AlphaPackagingTests
{
    [Fact] public void BuildInfo_exposes_alpha_version() { BuildInfo.AppName.Should().Be("LiteratureApp"); BuildInfo.Version.Should().Be("0.1.0-alpha-rc1"); BuildInfo.SchemaVersion.Should().Be(AppSchemaVersion.Current); }
    [Fact] public void AppRuntimeOptions_defaults_do_not_place_db_in_sync_root() { var o=AppRuntimeOptions.FromEnvironment(); Path.GetFullPath(o.RuntimeDatabasePath).Should().NotStartWith(Path.GetFullPath(o.DefaultSyncRoot)); o.UseMockOcrOnly.Should().BeTrue(); }
    [Fact] public void Logger_redacts_secret_values() { SimpleFileLogger.Redact("secret_value=sk-test-secret").Should().NotContain("sk-test-secret"); }
    [Fact] public void Logger_does_not_write_provider_secret() { SimpleFileLogger.Redact("api_key: provider-secret-value").Should().NotContain("provider-secret-value"); }
    [Fact] public void Alpha_smoke_script_exists() { File.Exists(TestPaths.FromRepositoryRoot("scripts","alpha-smoke-test.sh")).Should().BeTrue(); }
    [Fact] public void Alpha_risk_checklist_exists() { File.Exists(TestPaths.FromRepositoryRoot("docs","ALPHA_RISK_CHECKLIST.md")).Should().BeTrue(); }
    [Fact] public void Alpha_regression_checklist_exists() { File.Exists(TestPaths.FromRepositoryRoot("docs","ALPHA_REGRESSION_CHECKLIST.md")).Should().BeTrue(); }
    [Fact] public void README_contains_alpha_quick_start() { var r=File.ReadAllText(TestPaths.FromRepositoryRoot("README.md")); r.Should().Contain("Alpha Quick Start").And.Contain("dotnet publish"); }
    [Fact] public void README_states_real_ocr_and_credential_encryption_not_implemented() { var r=File.ReadAllText(TestPaths.FromRepositoryRoot("README.md")); r.Should().Contain("real OCR").And.Contain("credential encryption"); }
    [Fact] public void UI_version_info_viewmodel_exposes_version_schema_db_path() { var vm=new MainWindowViewModel(new TestClipboard()); vm.VersionInfo.Should().Contain("0.1.0-alpha").And.Contain("Schema").And.Contain(vm.RuntimeDatabasePath); }
    [Fact] public void README_documents_queue_runtime_priority_and_mcp_boundary() { var r=File.ReadAllText(TestPaths.FromRepositoryRoot("README.md")); r.Should().Contain("OCR Queue Scheduler").And.Contain("runtime-only").And.Contain("interactive_current_page").And.Contain("MCP cannot inspect or control the queue"); }
    [Fact] public void KnownIssues_mentions_queue_runtime_and_no_preset_pause() { var r=File.ReadAllText(TestPaths.FromRepositoryRoot("docs","KNOWN_ISSUES_ALPHA.md")); r.Should().Contain("runtime-only").And.Contain("preset-level pause"); }
    [Fact] public void README_documents_query_rewrite_and_no_semantic_search() { var r=File.ReadAllText(TestPaths.FromRepositoryRoot("README.md")); r.Should().Contain("Search Query Rewrite").And.Contain("canonical").And.Contain("no semantic/vector search"); }
    [Fact] public void BuildInfo_exposes_alpha_rc_version() { BuildInfo.Version.Should().Be("0.1.0-alpha-rc1"); }
    [Fact] public void Alpha_rc_docs_and_script_exist() { File.Exists(TestPaths.FromRepositoryRoot("scripts","alpha-rc-smoke-test.sh")).Should().BeTrue(); File.Exists(TestPaths.FromRepositoryRoot("docs","ALPHA_RC_CHECKLIST.md")).Should().BeTrue(); File.Exists(TestPaths.FromRepositoryRoot("docs","ALPHA_DATA_SAFETY_AUDIT.md")).Should().BeTrue(); File.Exists(TestPaths.FromRepositoryRoot("docs","ALPHA_RELEASE_NOTES.md")).Should().BeTrue(); }
    [Fact] public void Rc_readme_and_known_issues_document_boundaries() { var r=File.ReadAllText(TestPaths.FromRepositoryRoot("README.md")); r.Should().Contain("Alpha RC").And.Contain("alpha-rc-smoke-test.sh"); var k=File.ReadAllText(TestPaths.FromRepositoryRoot("docs","KNOWN_ISSUES_ALPHA.md")); k.Should().Contain("cloud OCR").And.Contain("automatic branch merge"); }
    private sealed class TestClipboard : IClipboardService { public Task SetTextAsync(string text)=>Task.CompletedTask; }
}
