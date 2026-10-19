using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK063: a <c>Fuzzy(...)</c> query against an index with no searchable and no <c>[Fuzzy]</c> field (commerce X18).
/// </summary>
/// <remarks>
/// <para>
/// Corax, the server default on Community and Developer licences, refuses fuzzy queries at runtime
/// (<c>InvalidQueryException: Method 'Fuzzy' is not supported</c>, measured on 7.2.5). Spark deploys an index on
/// Lucene when it has a searchable field (fuzzy by default) or a field opted in with <c>[Fuzzy]</c>, so a fuzzy
/// query against any other index compiles, deploys and then throws on its first call. A dynamic query (no index
/// named) runs on an auto index, which takes the server's auto-index engine and is flagged too.
/// </para>
/// <para>
/// The rule follows the query back to the <c>DocumentQuery</c> / <c>AsyncDocumentQuery</c> call that started it,
/// within one fluent chain. A query built elsewhere and passed in is not followed: the rule never guesses, so
/// it never fires on an index it cannot name. An index counts as Lucene when a <c>[FromIndex]</c> projection of it,
/// or a type argument of its base (the mapped document of a generated index, the result of a declared
/// multi-map), opts a property in with <c>[Fuzzy]</c> (not <c>[Fuzzy(false)]</c>), when its own source declares a
/// field <c>FieldIndexing.Search</c>, or when its constructor sets <c>SearchEngineType</c> to Lucene (which a generated
/// index over a <c>[Search]</c> property does).
/// An index whose constructor pins <c>SearchEngineType.Corax</c> is never Lucene: Spark keeps that pin for an
/// index that is only searchable. Raw RQL (<c>RawQuery("… fuzzy(…)")</c>) is a string and is not inspected.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FuzzyQueryAnalyzer : DiagnosticAnalyzer
{
    private const string FuzzyAttributeMetadataName = "MintPlayer.Spark.Abstractions.FuzzyAttribute";
    private const string FromIndexAttributeMetadataName = "MintPlayer.Spark.Abstractions.FromIndexAttribute";
    private const string SessionNamespace = "Raven.Client.Documents.Session";

    internal static readonly DiagnosticDescriptor FuzzyOnUnmarkedIndexRule = new(
        id: "SPARK063",
        title: "Fuzzy query against an index with no searchable or [Fuzzy] field",
        messageFormat: "Fuzzy(...) runs against {0}, which has no searchable or [Fuzzy] field (or pins Corax), so it is not deployed on Lucene; Corax refuses fuzzy queries at runtime. Mark the queried field [Search] or [Fuzzy] so Spark deploys the index on Lucene.",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Corax (the default engine on Community and Developer servers) throws 'Method Fuzzy is not supported'. Spark puts an index on Lucene when it has a searchable (FieldIndexing.Search) field or a field marked [Fuzzy]; an auto index takes the server default.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [FuzzyOnUnmarkedIndexRule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(start =>
        {
            var fuzzyAttribute = start.Compilation.GetTypeByMetadataName(FuzzyAttributeMetadataName);
            if (fuzzyAttribute is null) return;

            var fromIndexAttribute = start.Compilation.GetTypeByMetadataName(FromIndexAttributeMetadataName);
            var marks = new IndexMarks(start.Compilation, fuzzyAttribute, fromIndexAttribute);

            start.RegisterOperationAction(op => Analyze(op, marks), OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, IndexMarks marks)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (method.Name != "Fuzzy" || method.ContainingNamespace?.ToDisplayString() != SessionNamespace) return;

        if (FindRoot(invocation.Instance) is not { } root) return;

        string? target;
        if (root.TargetMethod.TypeArguments.Length == 2 && root.TargetMethod.TypeArguments[1] is INamedTypeSymbol index)
        {
            if (marks.IsMarked(index, context.CancellationToken)) return;
            target = $"index '{index.Name}'";
        }
        else
        {
            var indexName = root.Arguments.FirstOrDefault(a => a.Parameter?.Name == "indexName")?.Value;
            if (indexName is null || indexName.ConstantValue is not { HasValue: true } constant) return;

            if (constant.Value is string name)
            {
                // RavenDB names an index after its class with '_' read as '/'.
                var named = marks.FindIndex(name.Replace('/', '_'), context.CancellationToken);
                if (named is null || marks.IsMarked(named, context.CancellationToken)) return;
                target = $"index '{named.Name}'";
            }
            else
            {
                target = "an auto index (no index named)";
            }
        }

        var location = invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
            ? access.Name.GetLocation()
            : invocation.Syntax.GetLocation();

        context.ReportDiagnostic(Diagnostic.Create(FuzzyOnUnmarkedIndexRule, location, target));
    }

    /// <summary>
    /// The <c>DocumentQuery</c> / <c>AsyncDocumentQuery</c> call at the start of the fluent chain, or
    /// <see langword="null"/> when the chain starts anywhere else (a local, a parameter, a field).
    /// </summary>
    private static IInvocationOperation? FindRoot(IOperation? receiver)
    {
        for (var current = receiver; current is not null;)
        {
            switch (current)
            {
                case IConversionOperation conversion:
                    current = conversion.Operand;
                    continue;

                case IInvocationOperation call when call.TargetMethod.Name is "DocumentQuery" or "AsyncDocumentQuery":
                    return call;

                case IInvocationOperation call when call.TargetMethod.ContainingNamespace?.ToDisplayString() == SessionNamespace:
                    current = call.Instance;
                    continue;

                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>Which indexes of this compilation are marked fuzzy. Computed once per compilation, on first use.</summary>
    private sealed class IndexMarks
    {
        private readonly Compilation compilation;
        private readonly INamedTypeSymbol fuzzyAttribute;
        private readonly INamedTypeSymbol? fromIndexAttribute;
        private readonly object gate = new();
        private Dictionary<string, INamedTypeSymbol>? indexesByName;
        private HashSet<INamedTypeSymbol>? projectedFuzzy;

        public IndexMarks(Compilation compilation, INamedTypeSymbol fuzzyAttribute, INamedTypeSymbol? fromIndexAttribute)
        {
            this.compilation = compilation;
            this.fuzzyAttribute = fuzzyAttribute;
            this.fromIndexAttribute = fromIndexAttribute;
        }

        public INamedTypeSymbol? FindIndex(string className, CancellationToken ct)
        {
            Scan(ct);
            return indexesByName!.TryGetValue(className, out var index) ? index : null;
        }

        /// <summary>Whether Spark deploys <paramref name="index"/> on Lucene, so a fuzzy query against it can run.</summary>
        public bool IsMarked(INamedTypeSymbol index, CancellationToken ct)
        {
            Scan(ct);

            // Spark keeps an explicit engine pin for an index that is only searchable.
            var engine = PinnedEngine(index, ct);
            if (engine is not null) return engine == "Lucene";

            if (projectedFuzzy!.Contains(index)) return true;

            for (var current = index.BaseType; current is not null; current = current.BaseType)
                foreach (var argument in current.TypeArguments.OfType<INamedTypeSymbol>())
                    if (HasFuzzyProperty(argument)) return true;

            return DeclaresSearchField(index, ct);
        }

        /// <summary>
        /// A property opting in with <c>[Fuzzy]</c>; the opt-out <c>[Fuzzy(false)]</c> does not count. A <c>[Search]</c>
        /// property is not read here: a generated index over it sets Lucene in its generated constructor, which
        /// <see cref="PinnedEngine"/> reads, while a hand-written index over the same type is searchable only through
        /// its own <c>Index(..., FieldIndexing.Search)</c> (<see cref="DeclaresSearchField"/>), as at runtime.
        /// </summary>
        private bool HasFuzzyProperty(INamedTypeSymbol type)
        {
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
                {
                    if (property.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, fuzzyAttribute)
                            && !(a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is false)))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The engine a hand-written <c>SearchEngineType = SearchEngineType.X</c> in the index's own source pins
        /// (<c>"Lucene"</c>, <c>"Corax"</c>), or <see langword="null"/> when it pins none.
        /// </summary>
        private static string? PinnedEngine(INamedTypeSymbol index, CancellationToken ct)
            => index.DeclaringSyntaxReferences
                .SelectMany(r => r.GetSyntax(ct).DescendantNodes().OfType<AssignmentExpressionSyntax>())
                .Where(a => a.Left is IdentifierNameSyntax { Identifier.Text: "SearchEngineType" }
                            && a.Right is MemberAccessExpressionSyntax { Name.Identifier.Text: "Lucene" or "Corax" })
                .Select(a => ((MemberAccessExpressionSyntax)a.Right).Name.Identifier.Text)
                .FirstOrDefault();

        /// <summary>A hand-written <c>FieldIndexing.Search</c> in the index's own source: a searchable field.</summary>
        private static bool DeclaresSearchField(INamedTypeSymbol index, CancellationToken ct)
            => index.DeclaringSyntaxReferences
                .SelectMany(r => r.GetSyntax(ct).DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                .Any(m => m.Name.Identifier.Text == "Search"
                          && m.Expression is IdentifierNameSyntax { Identifier.Text: "FieldIndexing" }
                              or MemberAccessExpressionSyntax { Name.Identifier.Text: "FieldIndexing" });

        private void Scan(CancellationToken ct)
        {
            lock (gate)
            {
                if (indexesByName is not null) return;

                var byName = new Dictionary<string, INamedTypeSymbol>(System.StringComparer.Ordinal);
                var fuzzy = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

                foreach (var type in Types(compilation.Assembly.GlobalNamespace, ct))
                {
                    if (!type.IsAbstract && IsIndexTask(type) && !byName.ContainsKey(type.Name))
                        byName.Add(type.Name, type);

                    if (fromIndexAttribute is null) continue;
                    var fromIndex = type.GetAttributes()
                        .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, fromIndexAttribute));
                    // [Fuzzy] only: [Search] on a hand-written projection declares nothing on the index (its
                    // Index(..., FieldIndexing.Search) does, which DeclaresSearchField reads).
                    if (fromIndex?.ConstructorArguments.FirstOrDefault().Value is INamedTypeSymbol projected && HasFuzzyProperty(type))
                        fuzzy.Add(projected);
                }

                projectedFuzzy = fuzzy;
                indexesByName = byName;
            }
        }

        private static bool IsIndexTask(INamedTypeSymbol type)
        {
            for (var current = type.BaseType; current is not null; current = current.BaseType)
                if (current.ContainingNamespace?.ToDisplayString() == "Raven.Client.Documents.Indexes"
                    && current.Name.StartsWith("Abstract", System.StringComparison.Ordinal))
                    return true;

            return false;
        }

        private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns, CancellationToken ct)
        {
            foreach (var member in ns.GetMembers())
            {
                ct.ThrowIfCancellationRequested();

                if (member is INamespaceSymbol nested)
                {
                    foreach (var type in Types(nested, ct)) yield return type;
                }
                else if (member is INamedTypeSymbol type)
                {
                    yield return type;
                    foreach (var inner in type.GetTypeMembers()) yield return inner;
                }
            }
        }
    }
}
