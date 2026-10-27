using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

/// <remarks>
/// Registry-backed component catalog (screenshot revision, issue #43): engine and text-layer
/// rows come from <see cref="IOcrEngineCatalog"/> declarations plus generation/source state —
/// listing must never activate packages, open native sessions or download files. Only the
/// status case activates one targeted custom package (compiled-extension boundary); the pure
/// discovery cases stay on the lightweight descriptor path.
/// </remarks>
[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class OcrComponentCatalogTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task Engine_catalog_lists_registry_components_without_activating_packages()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            services.TranslationExtensions.Manager.List().Should().BeEmpty();

            using OcrComponentCatalogSettingsViewModel catalog =
                new(main, ExtensionKind.Ocr);
            await catalog.LoadAsync();

            catalog.Components.Should().NotBeEmpty();
            OcrComponentRowViewModel mineru =
                catalog.Components.Should().ContainSingle(row => row.IsBuiltIn).Subject;
            mineru.Id.Should().Be(OcrEngineIds.MinerU);
            mineru.ExtensionId.Should().BeNull();
            mineru.CanShowSource.Should().BeFalse("MinerU is built-in, not an extension package");
            mineru.Status.Should().Be(OcrComponentStatus.BuiltIn);
            mineru.StatusText.Should().Contain("内置引擎");

            catalog.Components.Should().NotContain(row => row.Id == OcrEngineIds.Mock);
            catalog.Components.Should().NotContain(row => row.Id == OcrEngineIds.LocalPlaceholder,
                "development-only engines never reach the settings catalog");

            OcrComponentRowViewModel multimodal =
                catalog.Components.Single(row => row.ExtensionId == "ocr.multimodal");
            multimodal.Id.Should().Be(OcrEngineIds.MultimodalLlm);
            multimodal.CanShowSource.Should().BeTrue();
            multimodal.Status.Should().Be(OcrComponentStatus.Unactivated);
            multimodal.IsCustom.Should().BeFalse();
            catalog.Components.Should().Contain(row => row.ExtensionId == "ocr.rapidocr");
            catalog.Components.Should().Contain(row => row.ExtensionId == "ocr.ndlocr-lite");
            catalog.Components.Should().Contain(row => row.ExtensionId == "ocr.ndlkotenocr-lite");

            services.TranslationExtensions.Manager.List().Should()
                .BeEmpty("catalog discovery is declarative — never an activation");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Text_layer_catalog_lists_independent_providers_without_activating()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();

            using OcrComponentCatalogSettingsViewModel catalog =
                new(main, ExtensionKind.PdfTextLayer);
            await catalog.LoadAsync();

            catalog.Components.Select(row => row.ExtensionId).Should().BeEquivalentTo(
                PdfTextLayerPackageIds.NativeXyCut,
                PdfTextLayerPackageIds.NativeGapTree,
                PdfTextLayerPackageIds.VisualGuided);
            catalog.Components.Should().NotContain(row =>
                    row.Id == PdfTextLayerProviderIds.Disabled,
                "the host-only disabled provider is a preset choice, not a component");
            catalog.Components.Should().OnlyContain(row =>
                row.Status == OcrComponentStatus.Unactivated && row.CanShowSource &&
                !row.IsBuiltIn && !row.IsCustom);
            catalog.Components.Should().NotContain(row => row.Id == OcrEngineIds.MinerU,
                "engine and provider catalogs stay independent");

            services.TranslationExtensions.Manager.List().Should().BeEmpty();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Gear_routes_mineru_to_its_dialog_and_packages_to_the_generic_editor()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            await main.ServicesAsync();

            using OcrComponentCatalogSettingsViewModel catalog =
                new(main, ExtensionKind.Ocr);
            await catalog.LoadAsync();

            MinerUConfigurationDialogViewModel? mineruDialog = null;
            catalog.MinerUDialogShower = dialog =>
            {
                mineruDialog = dialog;
                return Task.CompletedTask;
            };
            List<ExtensionSettingsViewModel> editors = [];
            catalog.ComponentEditorShower = editor =>
            {
                editors.Add(editor);
                return Task.CompletedTask;
            };

            OcrComponentRowViewModel mineru = catalog.Components.Single(row => row.IsBuiltIn);
            await mineru.ConfigureCommand.ExecuteAsync();
            mineruDialog.Should().NotBeNull();
            mineruDialog!.MinerUModelVersionOptions.Should().BeEquivalentTo("vlm", "pipeline");
            editors.Should().BeEmpty("MinerU never opens the generic FSX editor");

            OcrComponentRowViewModel engine =
                catalog.Components.Single(row => row.ExtensionId == "ocr.multimodal");
            await engine.ConfigureCommand.ExecuteAsync();
            ExtensionSettingsViewModel editor = editors.Should().ContainSingle().Subject;
            editor.SelectedExtension!.Id.Should().Be("ocr.multimodal");
            editor.OpenWithSourceExpanded.Should().BeFalse();

            await engine.ShowSourceCommand.ExecuteAsync();
            editors.Should().HaveCount(2);
            ExtensionSettingsViewModel sourceEditor = editors.Last();
            sourceEditor.SelectedExtension!.Id.Should().Be("ocr.multimodal");
            sourceEditor.OpenWithSourceExpanded.Should()
                .BeTrue("the source command requests the expanded source pane");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Status_distinguishes_active_changed_unactivated_and_invalid_source()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            IExtensionManager manager = services.TranslationExtensions.Manager;

            services.OcrEngineRuntime.RegisterEngine("ui-catalog-engine", OcrPackageId,
                new OcrEngineDefinition("ui-catalog-engine", "界面测试引擎", "local_library",
                    "test", ["page_image"]));
            await manager.SaveSourceAsync(OcrPackageId, OcrPackage(4).Sources);
            ExtensionCheckResult activation =
                await services.TranslationExtensions.ActivateAsync(OcrPackage(4));
            activation.Succeeded.Should()
                .BeTrue(string.Join("; ", activation.Diagnostics.Select(item => item.Message)));

            using OcrComponentCatalogSettingsViewModel catalog =
                new(main, ExtensionKind.Ocr);
            await catalog.LoadAsync();

            OcrComponentRowViewModel row =
                catalog.Components.Single(component => component.ExtensionId == OcrPackageId);
            row.Id.Should().Be("ui-catalog-engine");
            row.Status.Should().Be(OcrComponentStatus.Active);
            row.StatusText.Should().Contain("已激活");

            // A different valid working source no longer matches the active fingerprint.
            await manager.SaveSourceAsync(OcrPackageId, OcrPackage(8).Sources);
            await catalog.LoadAsync();
            row = catalog.Components.Single(component => component.ExtensionId == OcrPackageId);
            row.Status.Should().Be(OcrComponentStatus.ActiveSourceChanged);
            row.StatusText.Should().Contain("工作源码不同");

            // A non-empty broken draft keeps the row inspectable instead of hiding it.
            await manager.SaveSourceAsync(OcrPackageId, new Dictionary<string, string>
            {
                ["main.fsx"] = "let broken = ("
            });
            await catalog.LoadAsync();
            row = catalog.Components.Single(component => component.ExtensionId == OcrPackageId);
            row.Status.Should().Be(OcrComponentStatus.SourceInvalid);
            row.CanShowSource.Should().BeTrue("invalid-source packages remain inspectable");

            // An engine-less custom package that was never activated reports honestly.
            ExtensionPackageSnapshot renamed =
                ExtensionEntryMetadata.RewriteId(OcrPackage(4), "ocr.never-activated");
            await manager.SaveSourceAsync("ocr.never-activated", renamed.Sources);
            await catalog.LoadAsync();
            OcrComponentRowViewModel neverActivated = catalog.Components.Single(component =>
                component.ExtensionId == "ocr.never-activated");
            neverActivated.Status.Should().Be(OcrComponentStatus.Unactivated);
            neverActivated.StatusText.Should().Contain("未激活");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Active_custom_package_with_broken_working_source_stays_in_catalog()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            IExtensionManager manager = services.TranslationExtensions.Manager;

            await manager.SaveSourceAsync(OcrPackageId, OcrPackage(4).Sources);
            ExtensionCheckResult activation =
                await services.TranslationExtensions.ActivateAsync(OcrPackage(4));
            activation.Succeeded.Should()
                .BeTrue(string.Join("; ", activation.Diagnostics.Select(item => item.Message)));

            // The working draft is now corrupted while the generation stays active.
            await manager.SaveSourceAsync(OcrPackageId, new Dictionary<string, string>
            {
                ["main.fsx"] = "let broken = ("
            });

            using OcrComponentCatalogSettingsViewModel catalog =
                new(main, ExtensionKind.Ocr);
            await catalog.LoadAsync();

            OcrComponentRowViewModel row =
                catalog.Components.Should().ContainSingle(component =>
                    component.ExtensionId == OcrPackageId).Subject;
            row.Status.Should().Be(OcrComponentStatus.SourceInvalid);
            row.IsCustom.Should().BeTrue();
            row.Name.Should().Be("界面测试引擎",
                "the registered engine definition still supplies identity when working metadata is broken");
            row.CanShowSource.Should().BeTrue();

            // A never-activated package with the same broken metadata cannot be classified and
            // stays out of the OCR catalog; the generic extension editor is its repair path.
            await manager.SaveSourceAsync("ocr.unclassified-broken", new Dictionary<string, string>
            {
                ["main.fsx"] = "let broken = ("
            });
            await catalog.LoadAsync();
            catalog.Components.Should().NotContain(component =>
                component.ExtensionId == "ocr.unclassified-broken");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Custom_unactivated_package_opens_the_editor_with_source()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            await services.TranslationExtensions.Manager.SaveSourceAsync(OcrPackageId,
                OcrPackage(4).Sources);

            using OcrComponentCatalogSettingsViewModel catalog =
                new(main, ExtensionKind.Ocr);
            await catalog.LoadAsync();
            OcrComponentRowViewModel row =
                catalog.Components.Single(component => component.ExtensionId == OcrPackageId);
            row.Status.Should().Be(OcrComponentStatus.Unactivated);
            row.IsCustom.Should().BeTrue();

            ExtensionSettingsViewModel? editor = null;
            catalog.ComponentEditorShower = opened =>
            {
                editor = opened;
                return Task.CompletedTask;
            };
            await row.ShowSourceCommand.ExecuteAsync();
            editor.Should().NotBeNull();
            editor!.SelectedExtension!.Id.Should().Be(OcrPackageId);
            editor.OpenWithSourceExpanded.Should().BeTrue();
            return 0;
        }, CancellationToken.None);
    }

    private const string OcrPackageId = "ocr.ui-catalog-test";

    private static ExtensionPackageSnapshot OcrPackage(int maximum)
    {
        string source = $$"""
                          open System.Threading
                          open System.Threading.Tasks
                          open Patchouli.Core.Results
                          open Patchouli.Extensions.Contracts
                          open Patchouli.Extensions.Sdk

                          [<Settings>]
                          type Settings = {
                              [<Field("区域并发数", Kind = ConfigValueKind.Integer)>]
                              [<Range(1, {{maximum}})>]
                              [<Default(2)>]
                              Limit: int
                          }

                          let private check (_config: ExtensionConfigSnapshot) (_settings: Settings)
                              (_resources: IExtensionResources) (_ct: CancellationToken) : Task<Result> =
                              Task.FromResult(Result.Success())

                          let private run (_settings: Settings) (_job: IOcrJob)
                              (_resources: IExtensionResources) (_ct: CancellationToken) : Task =
                              Task.CompletedTask

                          /// <summary>OCR 组件目录测试引擎。</summary>
                          /// <patchouli-extension id="{{OcrPackageId}}" kind="ocr" version="1.0.0" host-api="patchouli.extensions/3"><name>界面测试 OCR</name></patchouli-extension>
                          let extension : IOcrExtension =
                              Extension.createOcrEngine<Settings>
                                  (OcrEngineDefinition("ui-catalog-engine", "界面测试引擎",
                                      "local_library", "test", ["page_image"]))
                                  check
                                  run
                          """;
        return new ExtensionPackageSnapshot(OcrPackageId, new Dictionary<string, string>
        {
            ["main.fsx"] = source
        });
    }
}
