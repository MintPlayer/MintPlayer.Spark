using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// SPARK066: a stored <c>DateTime</c> in a project that references a relational store (#536 O18).
/// </summary>
/// <remarks>
/// <para>
/// A <c>datetime2</c> or <c>timestamp</c> column keeps the ticks only, so a relational store reads every
/// <c>DateTime</c> back with Kind Utc (O18, decided 2026-11-26): right for <c>DateTime.UtcNow</c>, wrong for a local or
/// unspecified value, which then serializes with <c>Z</c> and displays shifted. RavenDB keeps the kind, so the note
/// is raised only where the compilation sees <c>SparkRelationalDialect</c>, i.e. references a relational provider.
/// </para>
/// <para>
/// Stored means a public instance property of a type a <c>SparkContext</c> in this compilation exposes as
/// <c>IQueryable&lt;T&gt;</c>, or of one of its base types. A type only an entity library declares, with the
/// context in another project, gets no note in the library. The note is reported at the property, which is its
/// own symbol action's location, so the IDE shows it live (see <c>ValueObjectCompletenessAnalyzer.LocationFor</c>).
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RelationalDateTimeAnalyzer : DiagnosticAnalyzer
{
    private const string SparkContextFullName = "MintPlayer.Spark.SparkContext";
    private const string RelationalDialectFullName = "MintPlayer.Spark.Relational.SparkRelationalDialect";
    private const string QueryableMetadataName = "System.Linq.IQueryable`1";

    internal static readonly DiagnosticDescriptor Rule = new(
        id: "SPARK066",
        title: "A stored DateTime reads back as UTC on a relational store",
        messageFormat: "'{0}' is a DateTime: a relational store keeps no DateTimeKind and reads it back as UTC. Use DateTimeOffset for an instant, or DateOnly for a date.",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "SQL Server's datetime2 and PostgreSQL's timestamp keep a DateTime's ticks but not its Kind, so Spark reads every DateTime back as UTC. A value written as DateTime.UtcNow round-trips; a local or unspecified one is shown shifted. DateTimeOffset keeps the instant and DateOnly the calendar date on every store.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            var compilation = start.Compilation;
            if (compilation.GetTypeByMetadataName(RelationalDialectFullName) is null)
                return;
            if (compilation.GetTypeByMetadataName(SparkContextFullName) is not { } contextBase
                || compilation.GetTypeByMetadataName(QueryableMetadataName) is not { } queryable)
                return;

            var stored = new Lazy<ImmutableHashSet<INamedTypeSymbol>>(
                () => StoredTypes(compilation, contextBase, queryable), LazyThreadSafetyMode.ExecutionAndPublication);
            start.RegisterSymbolAction(symbol => AnalyzeProperty(symbol, stored), SymbolKind.Property);
        });
    }

    private static void AnalyzeProperty(SymbolAnalysisContext context, Lazy<ImmutableHashSet<INamedTypeSymbol>> stored)
    {
        var property = (IPropertySymbol)context.Symbol;
        if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public)
            return;
        if (!IsDateTime(property.Type) || !stored.Value.Contains(property.ContainingType.OriginalDefinition))
            return;
        if (property.Locations.FirstOrDefault(l => l.IsInSource) is not { } location)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, location, $"{property.ContainingType.Name}.{property.Name}"));
    }

    private static bool IsDateTime(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        return type.SpecialType == SpecialType.System_DateTime;
    }

    /// <summary>
    /// The element types of every <c>IQueryable&lt;T&gt;</c> property on the compilation's own <c>SparkContext</c>
    /// subclasses, with their base types: a document stores its base type's properties too.
    /// </summary>
    private static ImmutableHashSet<INamedTypeSymbol> StoredTypes(
        Compilation compilation, INamedTypeSymbol contextBase, INamedTypeSymbol queryable)
    {
        var stored = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var type in TypesOf(compilation.Assembly.GlobalNamespace))
        {
            if (!InheritsFrom(type, contextBase))
                continue;
            foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.Type is not INamedTypeSymbol { IsGenericType: true } propertyType
                    || !SymbolEqualityComparer.Default.Equals(propertyType.OriginalDefinition, queryable)
                    || propertyType.TypeArguments[0] is not INamedTypeSymbol root)
                    continue;
                for (var current = root; current is { SpecialType: not SpecialType.System_Object }; current = current.BaseType)
                    stored.Add(current.OriginalDefinition);
            }
        }
        return stored.ToImmutable();
    }

    private static IEnumerable<INamedTypeSymbol> TypesOf(INamespaceOrTypeSymbol container)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol ns)
            {
                foreach (var type in TypesOf(ns))
                    yield return type;
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in TypesOf(type))
                    yield return nested;
            }
        }
    }

    private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
                return true;
        }
        return false;
    }
}
