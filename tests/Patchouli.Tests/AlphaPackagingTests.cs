using FluentAssertions;
using Patchouli.Core;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class AlphaPackagingTests
{
    [Fact]
    public void BuildInfo_exposes_version()
    {
        BuildInfo.AppName.Should().Be("Patchouli.Net");
        BuildInfo.Version.Should().Be("0.2.5");
        BuildInfo.SchemaVersion.Should().Be(AppSchemaVersion.Current);
    }

    [Fact]
    public void AppRuntimeOptions_defaults_do_not_place_db_in_sync_root()
    {
        AppRuntimeOptions o = AppRuntimeOptions.FromAppSettings();
        Path.GetFullPath(o.RuntimeDatabasePath).Should().NotStartWith(Path.GetFullPath(o.DefaultSyncRoot));
        o.UseMockOcrOnly.Should().BeFalse();
    }

    [Fact]
    public void AppRuntimeOptions_reads_appsettings_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"patchouli-appsettings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path,
                """{"Patchouli":{"RuntimeDatabasePath":"C:/patchouli/runtime.sqlite","DefaultSyncRoot":"C:/patchouli/sync","DefaultStagingRoot":"C:/patchouli/staging","LogDirectory":"C:/patchouli/logs","UseMockOcrOnly":false},"MinerU":{"BaseUrl":"https://mineru.example.test","ModelVersion":"vlm","Token":"configured-token"}}""");
            PatchouliAppSettings settings = PatchouliAppSettings.Load(path);
            settings.Runtime.RuntimeDatabasePath.Should().Contain("patchouli");
            settings.Runtime.UseMockOcrOnly.Should().BeFalse();
            settings.MinerU.BaseUrl.Should().Be("https://mineru.example.test");
            settings.MinerU.Should().NotBeNull();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Runtime_code_does_not_read_environment_variables()
    {
        string[] files = Directory.GetFiles(TestPaths.FromRepositoryRoot("src"), "*.cs", SearchOption.AllDirectories);
        string source = string.Join("\n", files.Select(File.ReadAllText));
        source.Should().NotContain("GetEnvironmentVariable").And.NotContain("SetEnvironmentVariable");
    }

    [Fact]
    public void Logger_redacts_secret_values()
    {
        SimpleFileLogger.Redact("secret_value=sk-test-secret").Should().NotContain("sk-test-secret");
    }

    [Fact]
    public void Logger_does_not_write_provider_secret()
    {
        SimpleFileLogger.Redact("api_key: provider-secret-value").Should().NotContain("provider-secret-value");
    }

    [Fact]
    public void Agent_docs_live_under_agent_directory()
    {
        File.Exists(TestPaths.FromRepositoryRoot(".agent", "PRD.md")).Should().BeTrue();
        File.Exists(TestPaths.FromRepositoryRoot(".agent", "domain.md")).Should().BeTrue();
        Directory.Exists(TestPaths.FromRepositoryRoot("docs")).Should().BeFalse();
    }

    [Fact]
    public void UI_version_info_viewmodel_exposes_version_schema_db_path()
    {
        using TemporaryAppSettingsFile settings = new();
        MainWindowViewModel vm = new(new TestClipboard(), settingsPath: settings.Path);
        vm.VersionInfo.Should().Contain("0.2.5").And.Contain("Schema").And.Contain(vm.RuntimeDatabasePath);
    }

    [Fact]
    public void PRD_documents_queue_search_and_mcp_boundaries()
    {
        string r = File.ReadAllText(TestPaths.FromRepositoryRoot(".agent", "PRD.md"));
        r.Should().Contain("MCP 从不触发 OCR 或索引重建").And.Contain("搜索配置文件").And.Contain("本地 FTS 索引是可重建的本地缓存");
    }

    [Fact]
    public void BuildInfo_has_no_prerelease_suffix()
    {
        BuildInfo.Version.Should().Be("0.2.5");
    }

    [Fact]
    public void Macos_package_relocates_defaults_validates_plist_and_skips_signing()
    {
        string script = File.ReadAllText(TestPaths.FromRepositoryRoot("scripts", "package-macos.sh"));
        script.Should().Contain("mv \"$macos_dir/appsettings.json\" \"$resources_dir/appsettings.json\"").And
            .Contain("plutil -lint").And.NotContain("codesign").And.NotContain("entitlements");
    }

    [Fact]
    public void Release_workflow_packages_windows_and_macos_from_tags()
    {
        string workflow = File.ReadAllText(TestPaths.FromRepositoryRoot(".github", "workflows", "release.yml"));

        workflow.Should().Contain("tags:").And.Contain("scripts/package-windows.ps1").And
            .Contain("scripts/package-macos.sh osx-arm64").And.Contain("gh release create").And
            .Contain("contents: write").And.Contain("GH_REPO: ${{ github.repository }}").And
            .Contain("actions/checkout@v7").And.Contain("actions/setup-dotnet@v6").And
            .Contain("actions/upload-artifact@v7").And.Contain("actions/download-artifact@v8");
    }

    [Fact]
    public void Source_migrations_exclude_legacy_layout_schema()
    {
        string[] names = Directory.GetFiles(TestPaths.MigrationsDirectory, "*.sql")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();

        names.Should().Contain("005_create_pages_and_document_trees.sql");
        names.Should().NotContain([
            "005_create_pages_and_layout.sql",
            "014_add_table_cell_metadata.sql",
            "024_add_layout_revision_source_basis.sql"
        ]);
        names.Should().NotContain(name => name.Contains("layout", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Windows_package_cleans_publish_dir_and_install_migrations()
    {
        string script = File.ReadAllText(TestPaths.FromRepositoryRoot("scripts", "package-windows.ps1"));
        string iss = File.ReadAllText(TestPaths.FromRepositoryRoot("packaging", "windows", "Patchouli.Net.iss"));

        script.Should().Contain("Remove-Item -LiteralPath $publishDir -Recurse -Force");
        script.Should().Contain("legacy layout schema files");
        iss.Should().Contain("[InstallDelete]").And.Contain(@"{app}\migrations");
    }

    [Fact]
    public void Csl_runtime_uses_managed_fsharp_citeproc_and_keeps_rust_tool_conventions()
    {
        string packages = File.ReadAllText(TestPaths.FromRepositoryRoot("Directory.Packages.props"));
        string infrastructure = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.Infrastructure", "Patchouli.Infrastructure.csproj"));
        string windowsPackage = File.ReadAllText(TestPaths.FromRepositoryRoot("scripts", "package-windows.ps1"));
        string macosPackage = File.ReadAllText(TestPaths.FromRepositoryRoot("scripts", "package-macos.sh"));
        string rustTools = File.ReadAllText(TestPaths.FromRepositoryRoot("tools", "README.md"));

        packages.Should().Contain("Fsharp.Citeproc");
        infrastructure.Should().Contain("Fsharp.Citeproc");
        windowsPackage.Should().NotContain("patchouli-hayagriva");
        macosPackage.Should().NotContain("patchouli-hayagriva");
        rustTools.Should().Contain("typst/biblatex").And.Contain("tools/**/target/");
    }

    [Fact]
    public void Readme_documents_both_current_rust_release_helpers()
    {
        string readme = File.ReadAllText(TestPaths.FromRepositoryRoot("README.md"));

        readme.Should().Contain("tools/biblatex-helper")
            .And.Contain("typst/biblatex 0.12.0")
            .And.Contain("tools/patchouli-shell-sidecar")
            .And.Contain("Bashkit 0.14.4")
            .And.Contain("cargo build --release --manifest-path tools/biblatex-helper/Cargo.toml")
            .And.Contain("cargo build --release --manifest-path tools/patchouli-shell-sidecar/Cargo.toml");
    }

    [Fact]
    public void About_lists_fsharp_citeproc_instead_of_hayagriva()
    {
        using TemporaryAppSettingsFile settings = new();
        AboutViewModel about = new(new MainWindowViewModel(new TestClipboard(), settingsPath: settings.Path));

        about.ThirdPartyLibraries.Select(library => library.Name).Should()
            .Contain("Fsharp.Citeproc").And.NotContain("Hayagriva");
    }

    [Fact]
    public void About_lists_locked_rust_text_dependencies()
    {
        using TemporaryAppSettingsFile settings = new();
        AboutViewModel about = new(new MainWindowViewModel(new TestClipboard(), settingsPath: settings.Path));

        about.ThirdPartyLibraries.Should()
            .Contain(library => library.Name == "Bashkit 0.14.4" && library.License == "MIT")
            .And.Contain(library => library.Name == "typst/biblatex 0.12.0" &&
                                    library.License == "MIT OR Apache-2.0");
    }

    [Fact]
    public void Macos_plist_describes_supported_user_selected_locations()
    {
        string plist = File.ReadAllText(TestPaths.FromRepositoryRoot("packaging", "macos", "Info.plist.template"));
        foreach (string key in new[]
                 {
                     "NSDesktopFolderUsageDescription", "NSDocumentsFolderUsageDescription",
                     "NSDownloadsFolderUsageDescription", "NSNetworkVolumesUsageDescription",
                     "NSRemovableVolumesUsageDescription"
                 })
        {
            plist.Should().Contain($"<key>{key}</key>");
        }
    }

    [Fact]
    public void Macos_package_builds_and_bundles_filesystem_helper()
    {
        string script = File.ReadAllText(TestPaths.FromRepositoryRoot("scripts", "package-macos.sh"));
        string project = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.Infrastructure",
            "Patchouli.Infrastructure.csproj"));
        script.Should().Contain("patchouli-macos-fs").And.Contain("libpatchouli-macos-fs.dylib");
        script.Should().Contain("osx-arm64").And.Contain("fs_helper_arch=\"arm64\"").And
            .Contain("osx-x64").And.Contain("fs_helper_arch=\"x86_64\"");
        project.Should().Contain("'$(RuntimeIdentifier)' == 'osx-arm64'").And.Contain("arm64").And
            .Contain("'$(RuntimeIdentifier)' == 'osx-x64'").And.Contain("x86_64").And
            .Contain("-arch $(MacOSFsHelperArchitecture)");

        string helperSource = TestPaths.FromRepositoryRoot("tools", "patchouli-macos-fs", "patchouli_macos_fs.m");
        string helperHeader = TestPaths.FromRepositoryRoot("tools", "patchouli-macos-fs", "patchouli_macos_fs.h");
        File.Exists(helperSource).Should().BeTrue();
        File.Exists(helperHeader).Should().BeTrue();
    }

    [Fact]
    public void ADR_records_mineru_as_product_ocr_provider()
    {
        string adr =
            File.ReadAllText(TestPaths.FromRepositoryRoot(".agent", "adr",
                "0014-use-mineru-as-first-product-ocr-provider.md"));
        adr.Should().Contain("MinerU").And.Contain("first product OCR/layout path");
    }

    [Fact]
    public void UI_theme_avoids_incompatible_huskui_fluenticons_chain()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Patchouli.UI.csproj")).Should()
            .NotContain("Huskui.Avalonia");
        File.ReadAllText(TestPaths.FromRepositoryRoot("Directory.Packages.props")).Should()
            .NotContain("Huskui.Avalonia");
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "App.axaml")).Should()
            .NotContain("HuskuiTheme");
    }

    [Fact]
    public async Task AppServices_default_settings_register_product_ocr_adapters()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-appservices-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "runtime.sqlite");
        try
        {
            AppRuntimeOptions runtime = PatchouliAppSettings.Default().Runtime with
            {
                RuntimeDatabasePath = path, DefaultSyncRoot = Path.Combine(root, "sync"),
                DefaultStagingRoot = Path.Combine(root, "staging"), LogDirectory = Path.Combine(root, "logs"),
                UseMockOcrOnly = false
            };
            AppServices services =
                await AppServices.CreateAsync(path, PatchouliAppSettings.Default() with { Runtime = runtime });
            services.OcrAdapters.ListCapabilities().Select(x => x.EngineId).Should()
                .Equal(OcrEngineIds.MinerU, OcrEngineIds.MultimodalLlm);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class TestClipboard : IClipboardService
    {
        public Task SetTextAsync(string text)
        {
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync()
        {
            return Task.FromResult<string?>(null);
        }
    }
}
