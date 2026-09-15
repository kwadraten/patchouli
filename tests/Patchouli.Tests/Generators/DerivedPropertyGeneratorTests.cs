using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;
using Patchouli.UI.Generators;
using Xunit;

namespace Patchouli.Tests.Generators;

public class DerivedPropertyGeneratorTests
{
    // Required stub: CSharpSourceGeneratorTest<TGenerator> needs a V1 ISourceGenerator type parameter
    // for the class fixture; the actual incremental generator is injected via GetSourceGenerators().
    public class EmptyGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context)
        {
        }

        public void Execute(GeneratorExecutionContext context)
        {
        }
    }

    // NOTE: The test sources decorate ordinary (non-partial) properties with [ObservableProperty]
    // instead of using C# 13 partial properties. The generator under test only inspects the
    // resulting property symbols, and running CommunityToolkit.Mvvm's ObservablePropertyGenerator
    // inside the test compilation makes the framework require every Toolkit-generated file to be
    // declared as an expected source. A plain property keeps the generated-source assertions on
    // DerivedPropertyGenerator meaningful.
    private class TestVerifier : CSharpSourceGeneratorTest<EmptyGenerator, GeneratorTestVerifier>
    {
        public TestVerifier()
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80
                .AddPackages(ImmutableArray.Create(
                    new PackageIdentity("CommunityToolkit.Mvvm", "8.4.2")
                ));
        }

        // Parse as the newest C# the compiler supports so the sources are not artificially
        // restricted by the test framework's default language version.
        protected override ParseOptions CreateParseOptions()
        {
            return new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Diagnose);
        }

        protected override IEnumerable<Type> GetSourceGenerators()
        {
            yield return typeof(DerivedPropertyGenerator);
        }
    }

    // The generator always emits ExcludeFromDerivedGenerationAttribute.g.cs via
    // RegisterPostInitializationOutput, so every test must include it in expected sources.
    private static readonly string AttributeFileContent = "\n" +
                                                          "namespace Patchouli.UI\n" +
                                                          "{\n" +
                                                          "    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = false, AllowMultiple = false)]\n" +
                                                          "    internal sealed class ExcludeFromDerivedGenerationAttribute : System.Attribute\n" +
                                                          "    {\n" +
                                                          "    }\n" +
                                                          "}";

    private static Task RunTestAsync(
        string source,
        string? generatedSourceName,
        string? generatedSourceCode,
        params DiagnosticResult[] expectedDiagnostics)
    {
        return RunTestAsync(source, generatedSourceName, generatedSourceCode, null, null, expectedDiagnostics);
    }

    private static Task RunTestAsync(
        string source,
        string? generatedSourceName,
        string? generatedSourceCode,
        string? generatedSourceName2,
        string? generatedSourceCode2,
        params DiagnosticResult[] expectedDiagnostics)
    {
        TestVerifier test = new()
        {
            TestState = { Sources = { source } }
        };

        // Always register the post-init attribute file – it is always emitted.
        test.TestState.GeneratedSources.Add((
            typeof(DerivedPropertyGenerator),
            "ExcludeFromDerivedGenerationAttribute.g.cs",
            SourceText.From(AttributeFileContent, System.Text.Encoding.UTF8)));

        if (generatedSourceName != null && generatedSourceCode != null)
        {
            test.TestState.GeneratedSources.Add((
                typeof(DerivedPropertyGenerator),
                generatedSourceName,
                SourceText.From(generatedSourceCode.Replace("\r\n", "\n"), System.Text.Encoding.UTF8)));
        }

        if (generatedSourceName2 != null && generatedSourceCode2 != null)
        {
            test.TestState.GeneratedSources.Add((
                typeof(DerivedPropertyGenerator),
                generatedSourceName2,
                SourceText.From(generatedSourceCode2.Replace("\r\n", "\n"), System.Text.Encoding.UTF8)));
        }

        test.TestState.ExpectedDiagnostics.AddRange(expectedDiagnostics);
        return test.RunAsync();
    }

    [Fact]
    public async Task Transitive_And_Diamond()
    {
        string source = @"
using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;

namespace TestNamespace;

public partial class MyViewModel : ObservableObject
{
    [ObservableProperty]
    public string PropA { get; set; }
    [ObservableProperty]
    public string PropB { get; set; }
    [ObservableProperty]
    public string PropC { get; set; }

    public string Direct => PropA;
    public string Multi => PropA + PropB;
    public string Trans => Multi + PropC;
    public string Diamond => Direct + Multi;
}";
        // Only mutable sources (PropA/PropB/PropC) generate switch cases.
        // Multi is computed (get-only) and is not a switch source.
        // Topological order: Direct < Multi < Diamond < Trans  (alphabetical tie-break within same level)
        string expected = @"using System.ComponentModel;

namespace TestNamespace
{
    partial class MyViewModel
    {
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            switch (e.PropertyName)
            {
                case ""PropA"":
                    base.OnPropertyChanged(new PropertyChangedEventArgs(""Direct""));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(""Multi""));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(""Diamond""));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(""Trans""));
                    break;
                case ""PropB"":
                    base.OnPropertyChanged(new PropertyChangedEventArgs(""Multi""));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(""Diamond""));
                    base.OnPropertyChanged(new PropertyChangedEventArgs(""Trans""));
                    break;
                case ""PropC"":
                    base.OnPropertyChanged(new PropertyChangedEventArgs(""Trans""));
                    break;
            }
        }
    }
}
";
        await RunTestAsync(source, "MyViewModel_DerivedProperties.g.cs", expected);
    }

    [Fact]
    public async Task SafeNestedMember_Enum_And_ConditionalAccess()
    {
        // `Child?.Kind == MyEnum.A`:
        //   - Child is an [ObservableProperty] on the same instance → dependency on "Child"
        //   - ?.Kind is a MemberBindingExpression in a conditional access tail; the
        //     generator intentionally skips it (root dependency captured by Child)
        //   - MyEnum.A is a static enum constant → safe, no dependency
        // Expected: only "Child" drives "IsSafe".
        string source = @"
using CommunityToolkit.Mvvm.ComponentModel;

namespace TestNamespace;

public enum MyEnum { A, B }

public partial class MyViewModel : ObservableObject
{
    [ObservableProperty]
    public MyViewModel Child { get; set; }

    [ObservableProperty]
    public MyEnum Kind { get; set; }

    public bool IsSafe => Child?.Kind == MyEnum.A;
}";
        string expected = @"using System.ComponentModel;

namespace TestNamespace
{
    partial class MyViewModel
    {
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            switch (e.PropertyName)
            {
                case ""Child"":
                    base.OnPropertyChanged(new PropertyChangedEventArgs(""IsSafe""));
                    break;
            }
        }
    }
}
";
        await RunTestAsync(source, "MyViewModel_DerivedProperties.g.cs", expected);
    }

    [Fact]
    public async Task PTCH001_Cycle()
    {
        string source = @"
using CommunityToolkit.Mvvm.ComponentModel;

namespace TestNamespace;

public partial class MyViewModel : ObservableObject
{
    [ObservableProperty]
    public string PropA { get; set; }

    public string PropB => PropC;
    public string PropC => PropB;
}";
        await RunTestAsync(source, null, null,
            DiagnosticResult.CompilerError("PTCH001").WithSpan(11, 19, 11, 24).WithArguments("PropB"));
    }

    [Fact]
    public async Task PTCH002_BackingFieldBypass()
    {
        string source = @"
using CommunityToolkit.Mvvm.ComponentModel;

namespace TestNamespace;

public partial class MyViewModel : ObservableObject
{
    [ObservableProperty]
    private string _propA;

    public string PropB => _propA;
}";
        await RunTestAsync(source, null, null,
            DiagnosticResult.CompilerWarning("PTCH002").WithSpan(11, 28, 11, 34).WithArguments("PropB", "_propA"));
    }

    [Fact]
    public async Task PTCH003_NonPartial()
    {
        string source = @"
using CommunityToolkit.Mvvm.ComponentModel;

namespace TestNamespace;

public class MyViewModel : ObservableObject
{
    [ObservableProperty]
    public string PropA { get; set; }

    public string PropB => PropA;
}";
        await RunTestAsync(source, null, null,
            DiagnosticResult.CompilerError("PTCH003").WithSpan(6, 14, 6, 25).WithArguments("MyViewModel"));
    }

    [Fact]
    public async Task PTCH004_ConflictingOverride()
    {
        string source = @"
using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;

namespace TestNamespace;

public partial class MyViewModel : ObservableObject
{
    [ObservableProperty]
    public string PropA { get; set; }

    public string PropB => PropA;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e) {}
}";
        await RunTestAsync(source, null, null,
            DiagnosticResult.CompilerError("PTCH004").WithSpan(7, 22, 7, 33).WithArguments("MyViewModel"));
    }

    [Fact]
    public async Task PTCH005_UnsafeGetter()
    {
        string source = @"
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace TestNamespace;

public partial class MyViewModel : ObservableObject
{
    [ObservableProperty]
    public string PropA { get; set; }

    public string PropB => PropA.ToString();
    public string PropC => DateTime.Now.ToString();
}";
        await RunTestAsync(source, null, null,
            DiagnosticResult.CompilerError("PTCH005").WithSpan(12, 19, 12, 24).WithArguments("PropB"),
            DiagnosticResult.CompilerError("PTCH005").WithSpan(13, 19, 13, 24).WithArguments("PropC"));
    }

    [Fact]
    public async Task Base_Mutable_Source_Drives_Derived_Computed_Property()
    {
        // The derived class declares no [ObservableProperty] member itself; participation comes
        // from the base class, and the base-declared mutable source must still drive the switch.
        string source = @"
using CommunityToolkit.Mvvm.ComponentModel;

namespace TestNamespace;

public partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    public string Source { get; set; }
}

public partial class DerivedViewModel : BaseViewModel
{
    public string Derived => Source;
}";
        // Only the derived class generates: it declares the only computed property.
        string expected = @"using System.ComponentModel;

namespace TestNamespace
{
    partial class DerivedViewModel
    {
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            switch (e.PropertyName)
            {
                case ""Source"":
                    base.OnPropertyChanged(new PropertyChangedEventArgs(""Derived""));
                    break;
            }
        }
    }
}
";
        await RunTestAsync(source, "DerivedViewModel_DerivedProperties.g.cs", expected);
    }

    [Fact]
    public async Task Cross_Layer_Chain_Each_Class_Notifies_Its_Own_Computed_Properties()
    {
        // Base: Source (mutable) and Middle => Source; Derived: Leaf => Middle.
        // The base override notifies Middle for ""Source""; the derived override must expand the
        // aggregated graph transitively and notify Leaf for ""Source"" without duplicating Middle.
        string source = @"
using CommunityToolkit.Mvvm.ComponentModel;

namespace TestNamespace;

public partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    public string Source { get; set; }

    public string Middle => Source;
}

public partial class DerivedViewModel : BaseViewModel
{
    public string Leaf => Middle;
}";
        string expectedBase = @"using System.ComponentModel;

namespace TestNamespace
{
    partial class BaseViewModel
    {
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            switch (e.PropertyName)
            {
                case ""Source"":
                    base.OnPropertyChanged(new PropertyChangedEventArgs(""Middle""));
                    break;
            }
        }
    }
}
";
        string expectedDerived = @"using System.ComponentModel;

namespace TestNamespace
{
    partial class DerivedViewModel
    {
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            switch (e.PropertyName)
            {
                case ""Source"":
                    base.OnPropertyChanged(new PropertyChangedEventArgs(""Leaf""));
                    break;
            }
        }
    }
}
";
        await RunTestAsync(source,
            "BaseViewModel_DerivedProperties.g.cs", expectedBase,
            "DerivedViewModel_DerivedProperties.g.cs", expectedDerived);
    }

    [Fact]
    public async Task Derived_Without_Own_ObservableProperty_Still_Generates()
    {
        // The participation gate is satisfied by the base class's [ObservableProperty] members
        // even when the derived class only declares plain mutable state of its own.
        string source = @"
using CommunityToolkit.Mvvm.ComponentModel;

namespace TestNamespace;

public partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    public string First { get; set; }

    [ObservableProperty]
    public string Second { get; set; }
}

public partial class DerivedViewModel : BaseViewModel
{
    public string Plain { get; set; } = """";

    public string Combined => First + Second;
}";
        string expected = @"using System.ComponentModel;

namespace TestNamespace
{
    partial class DerivedViewModel
    {
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            switch (e.PropertyName)
            {
                case ""First"":
                    base.OnPropertyChanged(new PropertyChangedEventArgs(""Combined""));
                    break;
                case ""Second"":
                    base.OnPropertyChanged(new PropertyChangedEventArgs(""Combined""));
                    break;
            }
        }
    }
}
";
        await RunTestAsync(source, "DerivedViewModel_DerivedProperties.g.cs", expected);
    }

    [Fact]
    public async Task PTCH001_Three_Node_Cycle()
    {
        string source = @"
using CommunityToolkit.Mvvm.ComponentModel;

namespace TestNamespace;

public partial class MyViewModel : ObservableObject
{
    [ObservableProperty]
    public string PropA { get; set; }

    public string A => B;
    public string B => C;
    public string C => A;
}";
        await RunTestAsync(source, null, null,
            DiagnosticResult.CompilerError("PTCH001").WithSpan(11, 19, 11, 20).WithArguments("A"));
    }
}
