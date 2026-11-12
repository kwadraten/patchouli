using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Host.Plugins;
using Patchouli.Mcp;
using Patchouli.Testing;

namespace Patchouli.Tests;

/// <summary>
/// Static plugin mounting tests: manifest TOML validation (path safety, reserved names,
/// duplicates, schema support, unsupported grants) and the dev-plugins.json registry gate.
/// All package work stays under .tmp/tests; nothing starts a plugin process.
/// </summary>
public sealed class PluginManifestAndRegistryTests
{
    private static readonly string TestRoot =
        Path.Combine(RepositoryTestEnvironment.TestRoot, "plugins-manifest");

    private static string NewPackage()
    {
        string directory = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "schemas"));
        Directory.CreateDirectory(Path.Combine(directory, ".agents"));
        File.WriteAllText(Path.Combine(directory, ".agents", "plugin-usage.md"), "# usage\n");
        File.WriteAllText(Path.Combine(directory, "schemas", "status.json"),
            """{"type":"object","required":["ok"],"properties":{"ok":{"type":"boolean"}}}""");
        File.WriteAllText(Path.Combine(directory, "schemas", "result.json"),
            """{"type":"object","required":["result_id","action"],"properties":{"result_id":{"type":"string"},"action":{"type":"string"}}}""");
        File.WriteAllText(Path.Combine(directory, "schemas", "classify.json"),
            """{"type":"object","additionalProperties":false,"required":["image_base64"],"properties":{"image_base64":{"type":"string","minLength":1},"media_type":{"type":"string","enum":["image/png","image/jpeg","image/webp"]},"top_k":{"type":"integer","minimum":1,"maximum":50}}}""");
        return directory;
    }

    private static string WriteManifest(string package, string toml)
    {
        string path = Path.Combine(package, PluginManifestLoader.ManifestFileName);
        File.WriteAllText(path, toml);
        return path;
    }

    private const string ValidManifest =
        """
        id = "kwadraten.kuzushi-classifier"
        version = "0.1.0"
        schema = "patchouli.plugin/1"
        host_api = "patchouli.plugins/1"
        name = "Kuzushi Classifier"
        description = "Classifier plugin"
        readme = ".agents/plugin-usage.md"

        [runtime]
        transport = "stdio"
        command = "dotnet"
        args = ["KuzushiClassifierApp.Plugin.dll"]

        [library]
        grants = []

        [[vfs.nodes]]
        path = ""
        kind = "directory"
        verbs = ["find","fetch"]
        summary = "root"

        [[vfs.nodes]]
        path = "status.json"
        kind = "file"
        verbs = ["fetch"]
        media = "application/json"
        schema = "schemas/status.json"

        [[vfs.nodes]]
        path = "results/"
        kind = "directory"
        verbs = ["find"]
        summary = "results"

        [[vfs.nodes]]
        path = "results/{result_id}.json"
        kind = "file"
        verbs = ["fetch"]
        media = "application/json"
        schema = "schemas/result.json"

        [[vfs.endpoints]]
        path = "control"

        [[vfs.endpoints.messages]]
        action = "classify"
        target_types = ["plugin-root"]
        input_schema = "schemas/classify.json"
        result_schema = "schemas/result.json"
        execution_mode = "sync"
        side_effects = "Creates an in-memory result."
        """;

    [Fact]
    public void Valid_manifest_loads_with_full_surface()
    {
        string package = NewPackage();
        Result<PluginDefinition> loaded = PluginManifestLoader.Load(WriteManifest(package, ValidManifest));

        loaded.IsSuccess.Should().BeTrue();
        PluginDefinition definition = loaded.Value;
        definition.Id.Should().Be("kwadraten.kuzushi-classifier");
        definition.Nodes.Should().HaveCount(4);
        definition.Endpoints.Should().HaveCount(1);
        definition.Endpoints[0].Messages[0].Action.Should().Be("classify");
        definition.DeclaredVerbs.Should().BeEquivalentTo("find", "fetch", "send");
        definition.FindNode("results/abc.json").Should().NotBeNull(); // template match
        definition.FindEndpoint("control").Should().NotBeNull();
    }

    [Fact]
    public void Manifest_rejections_are_explicit()
    {
        string package = NewPackage();
        (string Original, string Replacement, string Expected)[] cases =
        [
            ("version = \"0.1.0\"", "version = \"0.1\"", "SemVer"),
            ("schema = \"patchouli.plugin/1\"", "schema = \"patchouli.plugin/2\"", "not supported"),
            ("host_api = \"patchouli.plugins/1\"", "host_api = \"patchouli.plugins/2\"", "not supported"),
            ("id = \"kwadraten.kuzushi-classifier\"", "id = \"items\"", "reserved"),
            ("grants = []", "grants = [\"items:read\"]", "library grants")
        ];
        foreach ((string original, string replacement, string expected) in cases)
        {
            string variant = ValidManifest.Replace(original, replacement, StringComparison.Ordinal);
            Result<PluginDefinition> loaded =
                PluginManifestLoader.Load(WriteManifest(package, variant));
            loaded.IsFailure.Should().BeTrue($"{replacement}");
            loaded.ErrorMessage.Should().Contain(expected, replacement);
            File.Delete(Path.Combine(package, PluginManifestLoader.ManifestFileName));
        }
    }

    [Fact]
    public void Node_path_safety_and_conflicts_are_rejected()
    {
        string package = NewPackage();
        string[] badPaths =
        [
            "../escape.json", "a\\b.json", "a%20b.json", "plugin.yml", "README.md",
            "files/managed.bin", "a//b.json"
        ];
        foreach (string path in badPaths)
        {
            string manifest = ValidManifest.Replace("path = \"status.json\"",
                $"path = \"{path}\"", StringComparison.Ordinal);
            PluginManifestLoader.Load(WriteManifest(package, manifest)).IsFailure
                .Should().BeTrue($"path '{path}' must be rejected");
            File.Delete(Path.Combine(package, PluginManifestLoader.ManifestFileName));
        }

        string duplicate = ValidManifest.Replace("path = \"results/\"", "path = \"status.json\"",
            StringComparison.Ordinal).Replace("kind = \"directory\"\nverbs = [\"find\"]",
            "kind = \"file\"\nverbs = [\"fetch\"]", StringComparison.Ordinal);
        PluginManifestLoader.Load(WriteManifest(package, duplicate)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Unsupported_verbs_schemas_and_capabilities_fail_closed()
    {
        string package = NewPackage();
        string badVerb = ValidManifest.Replace("verbs = [\"find\",\"fetch\"]",
            "verbs = [\"find\",\"add\"]", StringComparison.Ordinal);
        PluginManifestLoader.Load(WriteManifest(package, badVerb)).IsFailure.Should().BeTrue();
        File.Delete(Path.Combine(package, PluginManifestLoader.ManifestFileName));

        File.WriteAllText(Path.Combine(package, "schemas", "status.json"),
            """{"type":"object","patternProperties":{"^x":{"type":"string"}}}""");
        PluginManifestLoader.Load(WriteManifest(package, ValidManifest)).IsFailure.Should().BeTrue();
        File.Delete(Path.Combine(package, PluginManifestLoader.ManifestFileName));

        string capabilities = ValidManifest + "\n[capabilities]\nmanaged_files = true\n";
        PluginManifestLoader.Load(WriteManifest(package, capabilities)).IsFailure.Should().BeTrue();
        File.Delete(Path.Combine(package, PluginManifestLoader.ManifestFileName));

        string missingSchema = ValidManifest.Replace("schema = \"schemas/status.json\"",
            "schema = \"schemas/missing.json\"", StringComparison.Ordinal);
        PluginManifestLoader.Load(WriteManifest(package, missingSchema)).IsFailure.Should().BeTrue();
        File.Delete(Path.Combine(package, PluginManifestLoader.ManifestFileName));

        string missingReadme = ValidManifest.Replace("readme = \".agents/plugin-usage.md\"",
            "readme = \"docs/missing.md\"", StringComparison.Ordinal);
        PluginManifestLoader.Load(WriteManifest(package, missingReadme)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Put_verb_is_accepted_on_file_and_directory_nodes()
    {
        string package = NewPackage();

        string filePut = ValidManifest.Replace(
            "verbs = [\"fetch\"]\n        media = \"application/json\"\n        schema = \"schemas/status.json\"",
            "verbs = [\"fetch\",\"put\"]\n        media = \"application/json\"\n        schema = \"schemas/status.json\"",
            StringComparison.Ordinal);
        Result<PluginDefinition> loaded = PluginManifestLoader.Load(WriteManifest(package, filePut));
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.DeclaredVerbs.Should().Contain("put");
        loaded.Value.FindNode("status.json")!.Verbs.Should().BeEquivalentTo("fetch", "put");
        File.Delete(Path.Combine(package, PluginManifestLoader.ManifestFileName));

        string directoryPut = ValidManifest.Replace(
            "path = \"results/\"\n        kind = \"directory\"\n        verbs = [\"find\"]",
            "path = \"results/\"\n        kind = \"directory\"\n        verbs = [\"find\",\"put\"]",
            StringComparison.Ordinal);
        Result<PluginDefinition> directoryLoaded =
            PluginManifestLoader.Load(WriteManifest(package, directoryPut));
        directoryLoaded.IsSuccess.Should().BeTrue();
        directoryLoaded.Value.FindNode("results/")!.Verbs.Should().BeEquivalentTo("find", "put");
        File.Delete(Path.Combine(package, PluginManifestLoader.ManifestFileName));

        // find stays directory-only: a file node may declare fetch/put, never find.
        string fileFind = ValidManifest.Replace(
            "verbs = [\"fetch\"]\n        media = \"application/json\"\n        schema = \"schemas/status.json\"",
            "verbs = [\"find\",\"put\"]\n        media = \"application/json\"\n        schema = \"schemas/status.json\"",
            StringComparison.Ordinal);
        PluginManifestLoader.Load(WriteManifest(package, fileFind)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Registry_records_never_grant_on_invalid_input()
    {
        string directory = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string registry = Path.Combine(directory, "dev-plugins.json");

        PluginRegistry.Load(registry).Entries.Should().BeEmpty(); // missing file: nothing granted

        File.WriteAllText(registry, "not json");
        PluginRegistrySnapshot malformed = PluginRegistry.Load(registry);
        malformed.Entries.Should().BeEmpty();
        malformed.Diagnostics.Should().NotBeEmpty();

        File.WriteAllText(registry,
            """[{"manifest_path":"relative/plugin.toml","enabled":true,"allowed_verbs":["find"]}]""");
        PluginRegistrySnapshot relative = PluginRegistry.Load(registry);
        relative.Entries.Should().BeEmpty();
        relative.Diagnostics.Should().NotBeEmpty();

        File.WriteAllText(registry,
            """[{"manifest_path":"D:/abs/plugin.toml","enabled":false,"allowed_verbs":["find"]}]""");
        PluginRegistry.Load(registry).Entries.Should().BeEmpty(); // disabled: no grant

        File.WriteAllText(registry,
            """[{"manifest_path":"D:/abs/plugin.toml","enabled":true,"allowed_verbs":["find","execute"]}]""");
        PluginRegistrySnapshot badVerb = PluginRegistry.Load(registry);
        badVerb.Entries.Should().BeEmpty();
        badVerb.Diagnostics.Should().NotBeEmpty();

        File.WriteAllText(registry,
            """[{"manifest_path":"D:/abs/plugin.toml","enabled":true,"allowed_verbs":["find","send"]}]""");
        PluginRegistrySnapshot valid = PluginRegistry.Load(registry);
        valid.Entries.Should().HaveCount(1);
        valid.Entries[0].AllowedVerbs.Should().BeEquivalentTo("find", "send");

        File.WriteAllText(registry,
            """[{"manifest_path":"D:/abs/plugin.toml","enabled":true,"allowed_verbs":["find","fetch","view"]}]""");
        PluginRegistrySnapshot withView = PluginRegistry.Load(registry);
        withView.Entries.Should().HaveCount(1);
        withView.Entries[0].AllowedVerbs.Should().BeEquivalentTo("find", "fetch", "view");

        File.WriteAllText(registry,
            """[{"manifest_path":"D:/abs/plugin.toml","enabled":true,"allowed_verbs":["find","fetch","put"]}]""");
        PluginRegistrySnapshot withPut = PluginRegistry.Load(registry);
        withPut.Entries.Should().HaveCount(1);
        withPut.Entries[0].AllowedVerbs.Should().BeEquivalentTo("find", "fetch", "put");
    }

    private const string ViewSourceNodes =
        """

        [[vfs.nodes]]
        path = "ui/lookup.html"
        kind = "file"
        verbs = ["fetch"]
        media = "text/html"

        [[vfs.nodes]]
        path = "ui/panel.html"
        kind = "file"
        verbs = ["fetch"]
        media = "text/html"
        """;

    private const string ViewsSection =
        """

        [ui]
        [[ui.views]]
        id = "lookup"
        title = "词条查询"
        placement = "pdf-sidebar"
        source = "ui/lookup.html"

        [[ui.views]]
        id = "inspector"
        title = "Inspector"
        placement = "tab"
        source = "ui/panel.html"

        [[ui.views]]
        id = "popup"
        title = "Popup"
        placement = "window"
        source = "ui/lookup.html"
        """;

    [Fact]
    public void Ui_views_load_for_all_placements_and_grant_the_view_verb()
    {
        string package = NewPackage();
        Result<PluginDefinition> loaded =
            PluginManifestLoader.Load(WriteManifest(package, ValidManifest + ViewSourceNodes + ViewsSection));

        loaded.IsSuccess.Should().BeTrue(loaded.ErrorMessage);
        PluginDefinition definition = loaded.Value;
        definition.Views.Select(view => (view.Id, view.Placement)).Should()
            .Equal(("lookup", "pdf-sidebar"), ("inspector", "tab"), ("popup", "window"));
        definition.Views[0].Title.Should().Be("词条查询");
        definition.Views[0].Source.Should().Be("ui/lookup.html");
        definition.DeclaredVerbs.Should().BeEquivalentTo("find", "fetch", "send", "view");
        definition.FindView("inspector").Should().NotBeNull();
        definition.FindView("missing").Should().BeNull();
    }

    [Fact]
    public void Absent_or_viewless_ui_section_declares_no_views()
    {
        string package = NewPackage();
        Result<PluginDefinition> plain =
            PluginManifestLoader.Load(WriteManifest(package, ValidManifest));
        plain.IsSuccess.Should().BeTrue();
        plain.Value.Views.Should().BeEmpty();
        plain.Value.DeclaredVerbs.Should().NotContain("view");

        Result<PluginDefinition> emptyUi =
            PluginManifestLoader.Load(WriteManifest(package, ValidManifest + "\n[ui]\n"));
        emptyUi.IsSuccess.Should().BeTrue();
        emptyUi.Value.Views.Should().BeEmpty();
        emptyUi.Value.DeclaredVerbs.Should().NotContain("view");
    }

    [Fact]
    public void Ui_view_validation_is_strict()
    {
        string package = NewPackage();
        string validView =
            """

            [ui]
            [[ui.views]]
            id = "lookup"
            title = "词条查询"
            placement = "pdf-sidebar"
            source = "ui/lookup.html"
            """;
        (string Original, string Replacement, string Expected)[] cases =
        [
            ("id = \"lookup\"", "id = \"1bad\"", "id"),
            ("id = \"lookup\"", "id = \"results/{result_id}.json\"", "id"),
            ("title = \"词条查询\"", "title = \"\"", "title"),
            ("placement = \"pdf-sidebar\"", "placement = \"overlay\"", "placement"),
            ("source = \"ui/lookup.html\"", "source = \"missing.html\"", "declared file node"),
            ("source = \"ui/lookup.html\"", "source = \"results/\"", "declared file node"),
            ("source = \"ui/lookup.html\"", "source = \"results/{result_id}.json\"", "declared file node")
        ];
        foreach ((string original, string replacement, string expected) in cases)
        {
            string variant = ValidManifest + ViewSourceNodes +
                             validView.Replace(original, replacement, StringComparison.Ordinal);
            Result<PluginDefinition> loaded =
                PluginManifestLoader.Load(WriteManifest(package, variant));
            loaded.IsFailure.Should().BeTrue($"{original} -> {replacement}");
            loaded.ErrorMessage.Should().Contain(expected, $"{original} -> {replacement}");
            File.Delete(Path.Combine(package, PluginManifestLoader.ManifestFileName));
        }

        string duplicate = ValidManifest + ViewSourceNodes + validView + validView.Replace(
            "[ui]\n", "", StringComparison.Ordinal);
        PluginManifestLoader.Load(WriteManifest(package, duplicate)).IsFailure.Should().BeTrue();
        File.Delete(Path.Combine(package, PluginManifestLoader.ManifestFileName));

        // Unknown keys inside a view entry are ignored, matching node parsing.
        string extraKey = ValidManifest + ViewSourceNodes +
                          validView.Replace("source = \"ui/lookup.html\"",
                              "source = \"ui/lookup.html\"\nexperimental = true", StringComparison.Ordinal);
        PluginManifestLoader.Load(WriteManifest(package, extraKey)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Views_namespace_is_reserved_for_nodes_and_endpoints()
    {
        string package = NewPackage();
        foreach (string path in new[] { "views", "views/", "views/lookup.html" })
        {
            string kind = path.EndsWith('/') ? "directory" : "file";
            string verbs = kind == "directory" ? "[\"find\"]" : "[\"fetch\"]";
            string manifest = ValidManifest.Replace("path = \"status.json\"", $"path = \"{path}\"",
                    StringComparison.Ordinal)
                .Replace(
                    "kind = \"file\"\nverbs = [\"fetch\"]\nmedia = \"application/json\"\nschema = \"schemas/status.json\"",
                    $"kind = \"{kind}\"\nverbs = {verbs}", StringComparison.Ordinal);
            PluginManifestLoader.Load(WriteManifest(package, manifest)).IsFailure
                .Should().BeTrue($"node path '{path}' must be rejected");
            File.Delete(Path.Combine(package, PluginManifestLoader.ManifestFileName));
        }

        foreach (string path in new[] { "views", "views/control" })
        {
            string manifest = ValidManifest.Replace("path = \"control\"", $"path = \"{path}\"",
                StringComparison.Ordinal);
            PluginManifestLoader.Load(WriteManifest(package, manifest)).IsFailure
                .Should().BeTrue($"endpoint path '{path}' must be rejected");
            File.Delete(Path.Combine(package, PluginManifestLoader.ManifestFileName));
        }
    }
}
