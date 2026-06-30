using FluentAssertions;
using Patchouli.Core;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class AlphaPackagingTests
{
    [Fact] public void BuildInfo_exposes_alpha_version() { BuildInfo.AppName.Should().Be("Patchouli"); BuildInfo.Version.Should().Be("0.1.0-alpha-rc1"); BuildInfo.SchemaVersion.Should().Be(AppSchemaVersion.Current); }
    [Fact] public void AppRuntimeOptions_defaults_do_not_place_db_in_sync_root() { var o=AppRuntimeOptions.FromAppSettings(); Path.GetFullPath(o.RuntimeDatabasePath).Should().NotStartWith(Path.GetFullPath(o.DefaultSyncRoot)); o.UseMockOcrOnly.Should().BeTrue(); }
    [Fact] public void AppRuntimeOptions_reads_appsettings_file() { var path=Path.Combine(Path.GetTempPath(),$"patchouli-appsettings-{Guid.NewGuid():N}.json"); try{File.WriteAllText(path,"""{"Patchouli":{"RuntimeDatabasePath":"C:/patchouli/runtime.sqlite","DefaultSyncRoot":"C:/patchouli/sync","DefaultStagingRoot":"C:/patchouli/staging","LogDirectory":"C:/patchouli/logs","UseMockOcrOnly":false},"MinerU":{"BaseUrl":"https://mineru.example.test","ModelVersion":"vlm","Token":"configured-token"}}"""); var settings=PatchouliAppSettings.Load(path); settings.Runtime.RuntimeDatabasePath.Should().Contain("patchouli"); settings.Runtime.UseMockOcrOnly.Should().BeFalse(); settings.MinerU.BaseUrl.Should().Be("https://mineru.example.test"); settings.MinerU.Token.Should().Be("configured-token");}finally{if(File.Exists(path))File.Delete(path);} }
    [Fact] public void Runtime_code_does_not_read_environment_variables() { var files=Directory.GetFiles(TestPaths.FromRepositoryRoot("src"),"*.cs",SearchOption.AllDirectories); var source=string.Join("\n",files.Select(File.ReadAllText)); source.Should().NotContain("GetEnvironmentVariable").And.NotContain("SetEnvironmentVariable"); }
    [Fact] public void Logger_redacts_secret_values() { SimpleFileLogger.Redact("secret_value=sk-test-secret").Should().NotContain("sk-test-secret"); }
    [Fact] public void Logger_does_not_write_provider_secret() { SimpleFileLogger.Redact("api_key: provider-secret-value").Should().NotContain("provider-secret-value"); }
    [Fact] public void Alpha_smoke_script_exists() { File.Exists(TestPaths.FromRepositoryRoot("scripts","alpha-smoke-test.sh")).Should().BeTrue(); }
    [Fact] public void Agent_docs_live_under_agent_directory() { File.Exists(TestPaths.FromRepositoryRoot(".agent","PRD.md")).Should().BeTrue(); File.Exists(TestPaths.FromRepositoryRoot(".agent","domain.md")).Should().BeTrue(); Directory.Exists(TestPaths.FromRepositoryRoot("docs")).Should().BeFalse(); }
    [Fact] public void UI_version_info_viewmodel_exposes_version_schema_db_path() { var vm=new MainWindowViewModel(new TestClipboard()); vm.VersionInfo.Should().Contain("0.1.0-alpha").And.Contain("Schema").And.Contain(vm.RuntimeDatabasePath); }
    [Fact] public void PRD_documents_queue_search_and_mcp_boundaries() { var r=File.ReadAllText(TestPaths.FromRepositoryRoot(".agent","PRD.md")); r.Should().Contain("MCP 从不触发 OCR 或索引重建").And.Contain("搜索配置文件").And.Contain("本地 FTS 索引是可重建的本地缓存"); }
    [Fact] public void BuildInfo_exposes_alpha_rc_version() { BuildInfo.Version.Should().Be("0.1.0-alpha-rc1"); }
    [Fact] public void Alpha_rc_script_exists_without_docs_dependency() { var script=File.ReadAllText(TestPaths.FromRepositoryRoot("scripts","alpha-rc-smoke-test.sh")); script.Should().Contain("dotnet restore").And.NotContain("docs/"); }
    [Fact] public void ADR_records_mineru_as_product_ocr_provider() { var adr=File.ReadAllText(TestPaths.FromRepositoryRoot(".agent","adr","0014-use-mineru-as-first-product-ocr-provider.md")); adr.Should().Contain("MinerU").And.Contain("first product OCR/layout path"); }
    [Fact] public void UI_theme_avoids_incompatible_huskui_fluenticons_chain() { File.ReadAllText(TestPaths.FromRepositoryRoot("src","Patchouli.UI","Patchouli.UI.csproj")).Should().NotContain("Huskui.Avalonia"); File.ReadAllText(TestPaths.FromRepositoryRoot("Directory.Packages.props")).Should().NotContain("Huskui.Avalonia"); File.ReadAllText(TestPaths.FromRepositoryRoot("src","Patchouli.UI","App.axaml")).Should().NotContain("HuskuiTheme"); }
    private sealed class TestClipboard : IClipboardService { public Task SetTextAsync(string text)=>Task.CompletedTask; }
}
