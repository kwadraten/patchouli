using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Patchouli.UI.Generators;

[Generator]
public class DerivedPropertyGenerator : IIncrementalGenerator
{
    private const string AttributeText = @"
namespace Patchouli.UI
{
    [System.AttributeUsage(System.AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
    internal sealed class ExcludeFromDerivedGenerationAttribute : System.Attribute
    {
    }
}";

    private static readonly DiagnosticDescriptor CycleError = new(
        "PTCH001", "Cycle detected in computed properties",
        "A cycle was detected involving property '{0}'", "Patchouli.UI", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor BackingFieldBypassWarning = new(
        "PTCH002", "Backing field bypass",
        "Computed property '{0}' accesses backing field '{1}' directly. Consider accessing the property instead.",
        "Patchouli.UI", DiagnosticSeverity.Warning, true);

    private static readonly DiagnosticDescriptor NonPartialClassError = new(
        "PTCH003", "Non-partial participating class",
        "Class '{0}' participates in derived property generation but is not partial", "Patchouli.UI",
        DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor ConflictingOverrideError = new(
        "PTCH004", "Conflicting OnPropertyChanged override",
        "Class '{0}' already overrides OnPropertyChanged(PropertyChangedEventArgs), which conflicts with generated code",
        "Patchouli.UI", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor UnsafeGetterError = new(
        "PTCH005", "Unsafe or unclassifiable computed getter",
        "Computed property '{0}' contains unsafe operations (methods, collections, etc.). Use [ExcludeFromDerivedGeneration] to ignore.",
        "Patchouli.UI", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx => ctx.AddSource("ExcludeFromDerivedGenerationAttribute.g.cs",
            SourceText.From(AttributeText.Replace("\r\n", "\n"), Encoding.UTF8)));

        IncrementalValuesProvider<ClassDeclarationSyntax?> classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (s, _) => s is ClassDeclarationSyntax,
                static (ctx, _) => GetSemanticTargetForGeneration(ctx))
            .Where(static m => m is not null);

        IncrementalValueProvider<(Compilation Left, ImmutableArray<ClassDeclarationSyntax?> Right)>
            compilationAndClasses = context.CompilationProvider.Combine(classDeclarations.Collect());

        context.RegisterSourceOutput(compilationAndClasses,
            static (spc, source) => Execute(source.Left, source.Right!, spc));
    }

    private static ClassDeclarationSyntax? GetSemanticTargetForGeneration(GeneratorSyntaxContext context)
    {
        ClassDeclarationSyntax classDeclaration = (ClassDeclarationSyntax)context.Node;
        // Simple heuristic to filter earlier if needed.
        return classDeclaration;
    }

    private static void Execute(Compilation compilation, ImmutableArray<ClassDeclarationSyntax> classes,
        SourceProductionContext context)
    {
        if (classes.IsDefaultOrEmpty)
        {
            return;
        }

        INamedTypeSymbol? excludeAttributeSymbol =
            compilation.GetTypeByMetadataName("Patchouli.UI.ExcludeFromDerivedGenerationAttribute");

        HashSet<INamedTypeSymbol> processedSymbols = new(SymbolEqualityComparer.Default);
        List<ClassDeclarationSyntax> sortedClasses = classes.Distinct().ToList();
        sortedClasses.Sort((a, b) => a.Identifier.Text.CompareTo(b.Identifier.Text));

        foreach (ClassDeclarationSyntax? classDeclaration in sortedClasses)
        {
            SemanticModel semanticModel = compilation.GetSemanticModel(classDeclaration.SyntaxTree);
            INamedTypeSymbol? classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration) as INamedTypeSymbol;
            if (classSymbol == null)
            {
                continue;
            }

            if (!processedSymbols.Add(classSymbol))
            {
                continue;
            }

            bool inheritsObservable = InheritsFrom(classSymbol, "ObservableObject", "ViewModelBase");
            if (!inheritsObservable)
            {
                continue;
            }

            ProcessClass(compilation, classSymbol, classDeclaration, excludeAttributeSymbol, context);
        }
    }

    private static bool InheritsFrom(INamedTypeSymbol symbol, params string[] names)
    {
        INamedTypeSymbol? current = symbol.BaseType;
        while (current != null)
        {
            if (names.Contains(current.Name))
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    private static void ProcessClass(
        Compilation compilation,
        INamedTypeSymbol classSymbol,
        ClassDeclarationSyntax classDeclaration,
        INamedTypeSymbol? excludeAttributeSymbol,
        SourceProductionContext context)
    {
        List<IPropertySymbol> allProperties = classSymbol.GetMembers().OfType<IPropertySymbol>().ToList();
        List<IPropertySymbol> computedProperties =
            allProperties.Where(p => p.GetMethod != null && p.SetMethod == null).ToList();

        if (computedProperties.Count == 0)
        {
            return;
        }

        // The dependency graph aggregates the whole inheritance chain: this class plus every
        // base class up to (excluding) ObservableObject. Base classes with source available are
        // getter-analyzed; metadata-only bases still contribute mutable source leaves.
        List<INamedTypeSymbol> chainTypes = new();
        for (INamedTypeSymbol? current = classSymbol;
             current != null && current.Name != "ObservableObject";
             current = current.BaseType)
        {
            chainTypes.Add(current);
        }

        bool hasObservableProperty = classDeclaration.Members.Any(m =>
            m.AttributeLists.Any(al => al.Attributes.Any(a => a.Name.ToString().Contains("ObservableProperty")))
        ) || chainTypes.Any(type => type.GetMembers().Any(member => member switch
        {
            IPropertySymbol property => property.GetAttributes()
                .Any(a => a.AttributeClass?.Name.Contains("ObservableProperty") == true),
            IFieldSymbol field => field.GetAttributes()
                .Any(a => a.AttributeClass?.Name.Contains("ObservableProperty") == true),
            _ => false
        }));

        if (!hasObservableProperty)
        {
            return;
        }

        bool isPartial = classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword);

        bool hasOverride = classSymbol.GetMembers().OfType<IMethodSymbol>()
            .Any(m => m.Name == "OnPropertyChanged" && m.IsOverride && m.Parameters.Length == 1 &&
                      m.Parameters[0].Type.Name == "PropertyChangedEventArgs");

        bool hasErrors = false;

        if (hasOverride)
        {
            context.ReportDiagnostic(Diagnostic.Create(ConflictingOverrideError,
                classDeclaration.Identifier.GetLocation(), classSymbol.Name));
            hasErrors = true;
        }

        Dictionary<string, HashSet<string>> dependencies = new(); // source -> derived
        HashSet<string> ownComputedNames = new(computedProperties.Select(p => p.Name));

        foreach (INamedTypeSymbol chainType in chainTypes)
        {
            bool isOwnType = SymbolEqualityComparer.Default.Equals(chainType, classSymbol);
            foreach (IPropertySymbol prop in chainType.GetMembers().OfType<IPropertySymbol>()
                         .Where(p => p.GetMethod != null && p.SetMethod == null)
                         .OrderBy(p => p.Name))
            {
                if (excludeAttributeSymbol != null && prop.GetAttributes().Any(a =>
                        SymbolEqualityComparer.Default.Equals(a.AttributeClass, excludeAttributeSymbol)))
                {
                    continue;
                }

                SyntaxReference? syntaxRef = prop.DeclaringSyntaxReferences.FirstOrDefault();
                if (syntaxRef == null)
                {
                    continue;
                }

                PropertyDeclarationSyntax? propSyntax = syntaxRef.GetSyntax() as PropertyDeclarationSyntax;
                if (propSyntax == null)
                {
                    continue;
                }

                AccessorDeclarationSyntax? getter =
                    propSyntax.AccessorList?.Accessors.FirstOrDefault(a => a.Keyword.IsKind(SyntaxKind.GetKeyword));
                SyntaxNode? bodyToAnalyze =
                    getter?.Body ?? (SyntaxNode?)getter?.ExpressionBody ?? propSyntax.ExpressionBody;

                if (bodyToAnalyze == null)
                {
                    continue;
                }

                // Inherited getters are re-analyzed for every participating derived class; only the
                // declaring class reports diagnostics so a base getter never produces duplicates.
                SemanticModel propSemanticModel = compilation.GetSemanticModel(propSyntax.SyntaxTree);
                GetterAnalyzer analyzer = new(propSemanticModel, classSymbol, context, prop,
                    propSyntax.Identifier.GetLocation(), isOwnType);
                analyzer.Visit(bodyToAnalyze);

                if (analyzer.HasErrors)
                {
                    hasErrors = true;
                }

                foreach (string? dep in analyzer.Dependencies)
                {
                    if (!dependencies.TryGetValue(dep, out HashSet<string>? set))
                    {
                        set = new HashSet<string>();
                        dependencies[dep] = set;
                    }

                    set.Add(prop.Name);
                }
            }
        }

        HashSet<string> ownComputedWithDeps = new();
        foreach (HashSet<string> targets in dependencies.Values)
        {
            foreach (string target in targets)
            {
                if (ownComputedNames.Contains(target))
                {
                    ownComputedWithDeps.Add(target);
                }
            }
        }

        if (!isPartial && ownComputedWithDeps.Count > 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(NonPartialClassError, classDeclaration.Identifier.GetLocation(),
                classSymbol.Name));
            hasErrors = true;
        }

        // Global Topological Sort
        Dictionary<string, int> inDegree = new();
        HashSet<string> allNodes = new();
        foreach (KeyValuePair<string, HashSet<string>> kvp in dependencies)
        {
            allNodes.Add(kvp.Key);
            if (!inDegree.ContainsKey(kvp.Key))
            {
                inDegree[kvp.Key] = 0;
            }

            foreach (string? child in kvp.Value)
            {
                allNodes.Add(child);
                if (!inDegree.ContainsKey(child))
                {
                    inDegree[child] = 0;
                }

                inDegree[child]++;
            }
        }

        List<string> queue = new();
        foreach (KeyValuePair<string, int> kvp in inDegree.OrderBy(k => k.Key))
        {
            if (kvp.Value == 0)
            {
                queue.Add(kvp.Key);
            }
        }

        List<string> topoOrder = new();
        Dictionary<string, int> topoIndex = new();

        int head = 0;
        while (head < queue.Count)
        {
            string? u = queue[head++];
            topoOrder.Add(u);
            topoIndex[u] = topoOrder.Count;

            if (dependencies.TryGetValue(u, out HashSet<string>? children))
            {
                foreach (string? v in children.OrderBy(c => c))
                {
                    inDegree[v]--;
                    if (inDegree[v] == 0)
                    {
                        queue.Add(v);
                    }
                }
            }
        }

        if (topoOrder.Count < allNodes.Count)
        {
            // Cycle found
            string? remaining = allNodes.Except(topoOrder).OrderBy(x => x).First();
            ISymbol? remainingSymbol = null;
            foreach (INamedTypeSymbol chainType in chainTypes)
            {
                remainingSymbol = chainType.GetMembers(remaining).FirstOrDefault();
                if (remainingSymbol != null)
                {
                    break;
                }
            }

            Location propLocation = remainingSymbol?.Locations.FirstOrDefault() ?? classDeclaration.GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(CycleError, propLocation, remaining));
            hasErrors = true;
        }

        if (hasErrors || ownComputedWithDeps.Count == 0)
        {
            return;
        }

        // Mutable sources come from the whole inheritance chain so a base-declared source still
        // drives notifications for derived computed properties.
        HashSet<string> mutableSourceNames = new(
            chainTypes.SelectMany(type => type.GetMembers().OfType<IPropertySymbol>())
                .Where(p => p.SetMethod != null)
                .Select(p => p.Name));

        Dictionary<string, List<string>> transitiveMap = new();
        foreach (string? src in dependencies.Keys.Where(k => mutableSourceNames.Contains(k)).OrderBy(k => k))
        {
            HashSet<string> visited = new();
            List<string> reach = new();

            void DFS(string node)
            {
                if (dependencies.TryGetValue(node, out HashSet<string>? children))
                {
                    foreach (string? c in children)
                    {
                        if (visited.Add(c))
                        {
                            reach.Add(c);
                            DFS(c);
                        }
                    }
                }
            }

            DFS(src);
            // Only this class's own computed properties are notified from this override; the base
            // class (when it also participates) notifies its own via its generated override.
            List<string> ownReach = reach.Where(ownComputedNames.Contains).ToList();
            if (ownReach.Count > 0)
            {
                ownReach.Sort((a, b) => topoIndex[a].CompareTo(topoIndex[b]));
                transitiveMap[src] = ownReach;
            }
        }

        if (transitiveMap.Count == 0)
        {
            return;
        }

        // Generate override
        StringBuilder sb = new();
        sb.AppendLine("using System.ComponentModel;");
        sb.AppendLine();

        string? ns = classSymbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : classSymbol.ContainingNamespace.ToDisplayString();
        if (ns != null)
        {
            sb.AppendLine($"namespace {ns}\n{{");
        }

        sb.AppendLine($"    partial class {classSymbol.Name}");
        sb.AppendLine("    {");
        sb.AppendLine("        protected override void OnPropertyChanged(PropertyChangedEventArgs e)");
        sb.AppendLine("        {");
        sb.AppendLine("            base.OnPropertyChanged(e);");
        sb.AppendLine();
        sb.AppendLine("            switch (e.PropertyName)");
        sb.AppendLine("            {");

        foreach (KeyValuePair<string, List<string>> kvp in transitiveMap.OrderBy(k => k.Key))
        {
            sb.AppendLine($"                case \"{kvp.Key}\":");
            foreach (string? derived in kvp.Value)
            {
                sb.AppendLine(
                    $"                    base.OnPropertyChanged(new PropertyChangedEventArgs(\"{derived}\"));");
            }

            sb.AppendLine("                    break;");
        }

        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");

        if (ns != null)
        {
            sb.AppendLine("}");
        }

        context.AddSource($"{classSymbol.Name}_DerivedProperties.g.cs",
            SourceText.From(sb.ToString().Replace("\r\n", "\n"), Encoding.UTF8));
    }

    private class GetterAnalyzer : CSharpSyntaxWalker
    {
        private readonly SemanticModel _semanticModel;
        private readonly INamedTypeSymbol _classSymbol;
        private readonly SourceProductionContext _context;
        private readonly IPropertySymbol _property;
        private readonly Location _location;
        private readonly bool _reportDiagnostics;

        public HashSet<string> Dependencies { get; } = new();
        public bool HasErrors { get; private set; }

        public GetterAnalyzer(SemanticModel semanticModel, INamedTypeSymbol classSymbol,
            SourceProductionContext context, IPropertySymbol property, Location location,
            bool reportDiagnostics)
        {
            _semanticModel = semanticModel;
            _classSymbol = classSymbol;
            _context = context;
            _property = property;
            _location = location;
            _reportDiagnostics = reportDiagnostics;
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (node.Expression is IdentifierNameSyntax id && id.Identifier.Text == "nameof")
            {
                return;
            }

            ReportUnsafe(node.GetLocation());
            base.VisitInvocationExpression(node);
        }

        public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
        {
            ReportUnsafe(node.GetLocation());
            base.VisitElementAccessExpression(node);
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            ISymbol? symbol = _semanticModel.GetSymbolInfo(node).Symbol;
            if (symbol != null)
            {
                if (IsStaticConstantOrEnum(symbol))
                {
                    return;
                }

                if (AnalyzeLocalSymbol(node, symbol))
                {
                    return;
                }

                if (IsSafeNestedMember(symbol))
                {
                    Visit(node.Expression);
                    return;
                }

                ReportUnsafe(node.GetLocation());
            }

            base.VisitMemberAccessExpression(node);
        }

        public override void VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
        {
            Visit(node.Expression);
            Visit(node.WhenNotNull);
        }

        public override void VisitMemberBindingExpression(MemberBindingExpressionSyntax node)
        {
            // This node is the `?.Member` part of a ConditionalAccessExpression.
            // The root dependency was already registered when VisitConditionalAccessExpression
            // visited node.Expression. Property and field reads here are safe; method
            // invocations (unsafe) are caught separately by VisitInvocationExpression.
            ISymbol? symbol = _semanticModel.GetSymbolInfo(node).Symbol;
            if (symbol is IPropertySymbol or IFieldSymbol)
            {
                return;
            }

            if (symbol != null && IsStaticConstantOrEnum(symbol))
            {
                return;
            }
            // Unrecognised symbol kinds: let children be visited normally.
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Parent is MemberAccessExpressionSyntax mae && mae.Name == node)
            {
                return;
            }

            if (node.Parent is MemberBindingExpressionSyntax mbe && mbe.Name == node)
            {
                return;
            }

            ISymbol? symbol = _semanticModel.GetSymbolInfo(node).Symbol;
            if (symbol != null)
            {
                if (IsStaticConstantOrEnum(symbol))
                {
                    return;
                }

                if (AnalyzeLocalSymbol(node, symbol))
                {
                    return;
                }

                if (node.Identifier.Text == "nameof")
                {
                    return;
                }

                ReportUnsafe(node.GetLocation());
            }

            base.VisitIdentifierName(node);
        }

        private bool IsSafeNestedMember(ISymbol symbol)
        {
            if (symbol is IPropertySymbol p)
            {
                return p.IsReadOnly;
            }

            if (symbol is IFieldSymbol f)
            {
                return f.IsReadOnly || f.IsConst;
            }

            return false;
        }

        private bool IsStaticConstantOrEnum(ISymbol symbol)
        {
            if (symbol.ContainingType?.TypeKind == TypeKind.Enum)
            {
                return true;
            }

            if (symbol is IFieldSymbol f && (f.IsConst || (f.IsStatic && f.IsReadOnly)))
            {
                return true;
            }

            if (symbol is IPropertySymbol p && p.IsStatic && p.IsReadOnly)
            {
                return true;
            }

            if (symbol is INamedTypeSymbol)
            {
                return true;
            }

            return false;
        }

        private bool AnalyzeLocalSymbol(SyntaxNode node, ISymbol symbol)
        {
            if (symbol is IPropertySymbol prop && IsMemberOfClassOrBase(prop.ContainingType))
            {
                if (!SymbolEqualityComparer.Default.Equals(prop, _property))
                {
                    Dependencies.Add(prop.Name);
                }

                return true;
            }

            if (symbol is IFieldSymbol field && IsMemberOfClassOrBase(field.ContainingType))
            {
                if (field.Name == "Empty" || field.IsStatic)
                {
                    return true;
                }

                if (_reportDiagnostics)
                {
                    _context.ReportDiagnostic(Diagnostic.Create(BackingFieldBypassWarning, node.GetLocation(),
                        _property.Name, field.Name));
                }

                return true;
            }

            return false;
        }

        private bool IsMemberOfClassOrBase(INamedTypeSymbol containingType)
        {
            INamedTypeSymbol? current = _classSymbol;
            while (current != null)
            {
                if (SymbolEqualityComparer.Default.Equals(current, containingType))
                {
                    return true;
                }

                current = current.BaseType;
            }

            return false;
        }

        private void ReportUnsafe(Location location)
        {
            if (!HasErrors)
            {
                if (_reportDiagnostics)
                {
                    _context.ReportDiagnostic(Diagnostic.Create(UnsafeGetterError, _location, _property.Name));
                }

                HasErrors = true;
            }
        }
    }
}
