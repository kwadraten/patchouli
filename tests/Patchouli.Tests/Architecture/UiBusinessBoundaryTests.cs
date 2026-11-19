using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests.Architecture;

/// <summary>
/// Issue #84 ratchet: compiled Patchouli.UI types may not gain dependencies on runtime-implementation
/// assemblies. Existing dependencies are an explicit, reviewable baseline that may only shrink.
/// </summary>
public sealed class UiBusinessBoundaryTests
{
    /// <summary>Implementation assemblies that business UI must eventually reach only through application contracts.</summary>
    internal static readonly IReadOnlySet<string> RestrictedAssemblies = new HashSet<string>(StringComparer.Ordinal)
    {
        "Patchouli.Host",
        "Patchouli.Infrastructure",
        "Patchouli.McpServer",
        "Patchouli.Mcp",
        "Patchouli.Ocr",
        "Patchouli.Ocr.Engines",
        "Patchouli.Llm",
        "Patchouli.Extensions"
    };

    /// <summary>
    /// Permanent startup-composition exceptions, kept per owner and target type. Business view models
    /// (including <c>MainWindowViewModel</c>) are never listed here; their current dependencies live only
    /// in the shrinking baseline file.
    /// </summary>
    internal static readonly IReadOnlySet<string> StartupCompositionExceptions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Patchouli.UI.Program -> Patchouli.Host!Patchouli.Host.Composition.HeadlessRuntimeHost",
            "Patchouli.UI.Program -> Patchouli.Host!Patchouli.UI.IAppPaths",
            "Patchouli.UI.Program -> Patchouli.Host!Patchouli.UI.PlatformAppPaths",
            "Patchouli.UI.Program -> Patchouli.Host!Patchouli.UI.Diagnostics.UnexpectedExceptions",
            "Patchouli.UI.Program -> Patchouli.Host!Patchouli.UI.Diagnostics.IUnexpectedExceptionSink"
        };

    internal const string BaselineFileName = "UiBusinessDependencyBaseline.txt";

    [Fact]
    public void Business_ui_does_not_gain_runtime_implementation_dependencies()
    {
        string assemblyPath = typeof(MainWindowViewModel).Assembly.Location;
        IReadOnlySet<TypeDependency> actual = AssemblyDependencyScanner.Scan(assemblyPath, RestrictedAssemblies);
        string[] baselineLines = File.ReadAllLines(TestPaths.FromRepositoryRoot(
            "tests", "Patchouli.Tests", "Architecture", BaselineFileName));

        string actualDump = TestPaths.FromRepositoryRoot(".tmp", "architecture", "ui-business-dependencies.actual.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(actualDump)!);
        File.WriteAllLines(actualDump, Format(actual.Where(d => !StartupCompositionExceptions.Contains(d.ToString()))));

        BoundaryComparison comparison = Compare(actual, ParseBaseline(baselineLines), StartupCompositionExceptions);

        comparison.Added.Should().BeEmpty(
            "business UI must not add dependencies on runtime implementation assemblies (issue #84); " +
            "route new work through an application use-case entry instead of extending the baseline");
        comparison.Stale.Should().BeEmpty(
            $"these dependencies were removed; delete them from {BaselineFileName} so the ratchet tightens");
        comparison.UnusedExceptions.Should().BeEmpty(
            "startup composition exceptions must stay as narrow as the code that still needs them");
    }

    [Fact]
    public void Baseline_is_sorted_unique_and_well_formed()
    {
        string[] lines = File.ReadAllLines(TestPaths.FromRepositoryRoot(
            "tests", "Patchouli.Tests", "Architecture", BaselineFileName));
        string[] entries = lines.Where(IsEntry).ToArray();

        string[] malformed = entries.Where(line => !TypeDependency.TryParse(line, out _)).ToArray();
        malformed.Should().BeEmpty();
        entries.Should().OnlyHaveUniqueItems();
        entries.Should().BeInAscendingOrder(StringComparer.Ordinal);
        ParseBaseline(entries).Select(dependency => dependency.TargetAssembly)
            .Should().OnlyContain(assembly => RestrictedAssemblies.Contains(assembly));
        entries.Should().NotIntersectWith(StartupCompositionExceptions);
    }

    [Fact]
    public void Scanner_detects_dependencies_hidden_by_aliases_full_names_inference_and_lambdas()
    {
        const string forbidden = """
                                 namespace Forbidden.Runtime
                                 {
                                     public sealed class Services { public Store Store => new(); public static Services Create() => new(); }
                                     public sealed class Store { public int Count => 0; }
                                     public sealed class Marker { }
                                     public sealed class Payload { }
                                     public sealed class Seen : System.Attribute { }
                                     public interface IGate { }
                                     public static class Factory { public static Payload Make() => new(); }
                                 }
                                 """;
        const string consumer = """
                                using Alias = Forbidden.Runtime.Marker;

                                namespace Fixture
                                {
                                    public sealed class FullyQualified { public object Make() => new Forbidden.Runtime.Marker(); }
                                    public sealed class Aliased { public object Make() => new Alias(); }
                                    public sealed class Inferred
                                    {
                                        // Store is only reachable through a member return type, never named in source.
                                        public int Count() { var store = Forbidden.Runtime.Services.Create().Store; return store.Count; }
                                    }
                                    public sealed class Captured
                                    {
                                        public System.Func<object> Make() => () => Forbidden.Runtime.Factory.Make();
                                    }
                                    public sealed class Generic { public System.Collections.Generic.List<Forbidden.Runtime.Marker> Items = new(); }
                                    [Forbidden.Runtime.Seen] public sealed class Attributed { }
                                    public sealed class Implements : Forbidden.Runtime.IGate { }
                                    public sealed class Clean { public int Value => 1; }
                                }
                                """;

        byte[] forbiddenImage = Compile("Forbidden.Runtime", forbidden);
        byte[] consumerImage = Compile("Fixture.Consumer", consumer, MetadataReference.CreateFromImage(forbiddenImage));

        IReadOnlySet<TypeDependency> found = AssemblyDependencyScanner.Scan(
            consumerImage, new HashSet<string>(StringComparer.Ordinal) { "Forbidden.Runtime" });
        HashSet<string> rendered = found.Select(d => d.ToString()).ToHashSet(StringComparer.Ordinal);

        rendered.Should().Contain([
            "Fixture.FullyQualified -> Forbidden.Runtime!Forbidden.Runtime.Marker",
            "Fixture.Aliased -> Forbidden.Runtime!Forbidden.Runtime.Marker",
            "Fixture.Inferred -> Forbidden.Runtime!Forbidden.Runtime.Store",
            "Fixture.Inferred -> Forbidden.Runtime!Forbidden.Runtime.Services",
            "Fixture.Captured -> Forbidden.Runtime!Forbidden.Runtime.Factory",
            "Fixture.Captured -> Forbidden.Runtime!Forbidden.Runtime.Payload",
            "Fixture.Generic -> Forbidden.Runtime!Forbidden.Runtime.Marker",
            "Fixture.Attributed -> Forbidden.Runtime!Forbidden.Runtime.Seen",
            "Fixture.Implements -> Forbidden.Runtime!Forbidden.Runtime.IGate"
        ]);
        found.Should().NotContain(d => d.Owner == "Fixture.Clean");
        found.Should().OnlyContain(d => !d.Owner.Contains('<') && !d.Owner.Contains('+'),
            "compiler-generated closures and state machines are attributed to their source owner");
    }

    [Fact]
    public void Comparison_reports_new_targets_new_owners_and_stale_entries_but_accepts_the_baseline()
    {
        TypeDependency existing = new("Ui.Vm", "Patchouli.Host", "Patchouli.Host.Composition.HostServices");
        TypeDependency startup = new("Patchouli.UI.Program", "Patchouli.Host",
            "Patchouli.Host.Composition.HeadlessRuntimeHost");
        TypeDependency newTarget = new("Ui.Vm", "Patchouli.Infrastructure",
            "Patchouli.Infrastructure.Database.SqliteConnectionFactory");
        TypeDependency newOwner = new("Ui.Other", "Patchouli.Host", "Patchouli.Host.Composition.HostServices");
        TypeDependency removed = new("Ui.Old", "Patchouli.Host", "Patchouli.Host.Mcp.McpServerHost");
        HashSet<string> exceptions = new(StringComparer.Ordinal) { startup.ToString() };

        BoundaryComparison accepted = Compare([existing, startup], [existing], exceptions);
        accepted.Added.Should().BeEmpty();
        accepted.Stale.Should().BeEmpty();
        accepted.UnusedExceptions.Should().BeEmpty();

        BoundaryComparison grown = Compare([existing, startup, newTarget, newOwner], [existing, removed], exceptions);
        grown.Added.Should().BeEquivalentTo([newTarget.ToString(), newOwner.ToString()]);
        grown.Stale.Should().BeEquivalentTo([removed.ToString()]);

        BoundaryComparison startupGone = Compare([existing], [existing], exceptions);
        startupGone.UnusedExceptions.Should().BeEquivalentTo([startup.ToString()]);
    }

    /// <summary>
    /// ADR 0058 reserves the <c>Patchouli.Remote</c> assembly prefix for remote transport adapters. The Host
    /// implements use cases and never depends on them; local desktop composition and business UI do not either,
    /// so the in-process path cannot be routed through HTTP, serialization or remote authentication.
    /// </summary>
    [Fact]
    public void Host_and_local_desktop_composition_do_not_reference_remote_adapters()
    {
        string[] assemblies =
        [
            typeof(MainWindowViewModel).Assembly.Location,
            typeof(Host.Composition.HostServices).Assembly.Location
        ];

        string[] offenders = assemblies
            .SelectMany(path => ReferencedAssemblyNames(File.ReadAllBytes(path))
                .Where(IsRemoteAdapterAssembly)
                .Select(reference => $"{Path.GetFileNameWithoutExtension(path)} -> {reference}"))
            .ToArray();

        offenders.Should().BeEmpty(
            "the local desktop uses the in-process use-case implementation; remote adapters are an explicit " +
            "Avalonia remote mode and need a reviewed composition exception (ADR 0058)");
    }

    [Fact]
    public void Remote_adapter_reference_check_detects_a_reserved_prefix_reference()
    {
        byte[] remote = Compile("Patchouli.Remote.Client",
            "namespace Patchouli.Remote.Client { public sealed class CatalogClient { } }");
        byte[] local = Compile("Fixture.Local",
            "namespace Fixture { public sealed class Wiring { public object Make() => new Patchouli.Remote.Client.CatalogClient(); } }",
            MetadataReference.CreateFromImage(remote));
        byte[] clean = Compile("Fixture.Clean", "namespace Fixture { public sealed class Wiring { } }");

        ReferencedAssemblyNames(local).Where(IsRemoteAdapterAssembly).Should().Equal("Patchouli.Remote.Client");
        ReferencedAssemblyNames(clean).Where(IsRemoteAdapterAssembly).Should().BeEmpty();
        IsRemoteAdapterAssembly("Patchouli.Remote").Should().BeTrue();
        IsRemoteAdapterAssembly("Patchouli.RemoteDesktopHelpers").Should().BeFalse();
    }

    internal static bool IsRemoteAdapterAssembly(string name)
    {
        return name == "Patchouli.Remote" || name.StartsWith("Patchouli.Remote.", StringComparison.Ordinal);
    }

    private static IEnumerable<string> ReferencedAssemblyNames(byte[] image)
    {
        using System.Reflection.PortableExecutable.PEReader pe =
            new(System.Collections.Immutable.ImmutableArray.Create(image));
        System.Reflection.Metadata.MetadataReader reader =
            System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        return reader.AssemblyReferences
            .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
            .ToArray();
    }

    [Fact]
    public void Startup_exceptions_cover_composition_entry_points_only()
    {
        StartupCompositionExceptions.Should().OnlyContain(entry =>
            entry.StartsWith("Patchouli.UI.Program -> ", StringComparison.Ordinal));
    }

    internal sealed record BoundaryComparison(
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Stale,
        IReadOnlyList<string> UnusedExceptions);

    internal static BoundaryComparison Compare(
        IEnumerable<TypeDependency> actual,
        IEnumerable<TypeDependency> baseline,
        IReadOnlySet<string> exceptions)
    {
        HashSet<string> actualSet = actual.Select(d => d.ToString()).ToHashSet(StringComparer.Ordinal);
        HashSet<string> baselineSet = baseline.Select(d => d.ToString()).ToHashSet(StringComparer.Ordinal);
        string[] added = actualSet.Where(e => !baselineSet.Contains(e) && !exceptions.Contains(e))
            .Order(StringComparer.Ordinal).ToArray();
        string[] stale = baselineSet.Where(e => !actualSet.Contains(e)).Order(StringComparer.Ordinal).ToArray();
        string[] unused = exceptions.Where(e => !actualSet.Contains(e)).Order(StringComparer.Ordinal).ToArray();
        return new BoundaryComparison(added, stale, unused);
    }

    private static IEnumerable<TypeDependency> ParseBaseline(IEnumerable<string> lines)
    {
        foreach (string line in lines.Where(IsEntry))
        {
            if (!TypeDependency.TryParse(line, out TypeDependency dependency))
            {
                throw new FormatException($"Malformed baseline entry: {line}");
            }

            yield return dependency;
        }
    }

    private static bool IsEntry(string line)
    {
        return !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#');
    }

    private static IEnumerable<string> Format(IEnumerable<TypeDependency> dependencies)
    {
        return dependencies.Select(d => d.ToString()).Order(StringComparer.Ordinal);
    }

    private static byte[] Compile(string name, string source, params MetadataReference[] extra)
    {
        string trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        IEnumerable<MetadataReference> runtime = trusted.Split(Path.PathSeparator)
            .Where(path => Path.GetFileName(path) is "System.Runtime.dll" or "System.Private.CoreLib.dll"
                or "System.Collections.dll")
            .Select(path => MetadataReference.CreateFromFile(path));
        CSharpCompilation compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText(source)],
            runtime.Concat(extra),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using MemoryStream image = new();
        EmitResult emitted = compilation.Emit(image);
        emitted.Success.Should().BeTrue(string.Join(Environment.NewLine, emitted.Diagnostics));
        return image.ToArray();
    }
}
