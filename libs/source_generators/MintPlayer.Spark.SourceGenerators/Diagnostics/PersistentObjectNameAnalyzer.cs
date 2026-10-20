using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using MintPlayer.Spark.Layering;
using MintPlayer.Spark.SourceGenerators.Generators;
using MintPlayer.Spark.SourceGenerators.Json;
using MintPlayer.Spark.SourceGenerators.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// Persistent-object name identity at build time (D28, X9; research §8.8, spike S11): the names the
/// run time composes are checked where the developer writes them, with the run time's own engine
/// (<see cref="SparkRenames"/>, <see cref="SparkModelLayers"/>, D14), so the build refuses what startup refuses.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>SPARK057 (error): two referenced libraries ship one effective name. Reported from a symbol action
/// on the application's <c>SparkContext</c> so the IDE offers the fix; <see cref="Location.None"/> without one.</item>
/// <item>SPARK058 (error): a context root's entity takes a library type's name with a different CLR class —
/// the synchronizer hijack (F7), predicted before any model file exists. On the property.</item>
/// <item>SPARK059 / SPARK060 (warnings): an application model file that names a library type without
/// <c>$extends</c> (or states another id or CLR type), or an orphaned / stale delta. They read the files
/// <c>--spark-synchronize-model</c> writes, which can lag the C# mid-build; an error would fail the very
/// build that command needs (S11), so <c>--spark-verify-model</c> and startup are the gates.</item>
/// <item>SPARK061 (error): an <c>[assembly: PersistentObjectName]</c> that cannot be honoured. A syntax-node
/// action, because symbol actions do not visit the assembly.</item>
/// <item>SPARK062 (warning): two Spark entity CLR types share a simple name, and with it the default RavenDB
/// collection (research §8.9).</item>
/// </list>
/// Actions and custom-action collisions stay with the run time: it scans assemblies the compiler never sees.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PersistentObjectNameAnalyzer : DiagnosticAnalyzer
{
    private const string SparkContextFullName = "MintPlayer.Spark.SparkContext";
    private const string FromIndexAttributeFullName = "MintPlayer.Spark.Abstractions.FromIndexAttribute";

    /// <summary>Diagnostic properties the code fixes read.</summary>
    internal const string AliasProperty = "Alias";
    internal const string DeclaredNameProperty = "DeclaredName";
    internal const string EntityTypeProperty = "EntityType";
    internal const string SuggestedNameProperty = "SuggestedName";

    internal static readonly DiagnosticDescriptor LibraryNameCollisionRule = new(
        id: "SPARK057",
        title: "Two referenced libraries ship a persistent-object type with the same name",
        messageFormat: "Libraries '{0}' and '{1}' both ship a persistent-object type named '{2}'. The application cannot start with both. Rename one: [assembly: PersistentObjectName(\"{3}\", \"{4}\", \"<NewName>\")].",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor ApplicationTypeTakesLibraryNameRule = new(
        id: "SPARK058",
        title: "An application entity takes the name of a library type",
        messageFormat: "'{0}' is named '{1}', the name of '{2}' ({3}). --spark-synchronize-model would turn it into a delta on the library's type: under the library's id and rights, while the library's row policies (keyed by CLR type) no longer apply. Name it apart: [assembly: PersistentObjectName<{4}>(\"<NewName>\")], or rename the library's type.",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnanchoredDeltaRule = new(
        id: "SPARK059",
        title: "A model file collides with a library type",
        messageFormat: "{0}",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    internal static readonly DiagnosticDescriptor OrphanedDeltaRule = new(
        id: "SPARK060",
        title: "A model delta is orphaned or stale",
        messageFormat: "{0}",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    internal static readonly DiagnosticDescriptor InvalidRenameRule = new(
        id: "SPARK061",
        title: "A persistent-object rename cannot be honoured",
        messageFormat: "{0}",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor SharedCollectionRule = new(
        id: "SPARK062",
        title: "Two entity classes share a simple name, and so a RavenDB collection",
        messageFormat: "'{0}' and '{1}' share the simple name '{2}', so both store their documents in the RavenDB collection RavenDB derives from it: querying one returns the other's documents, and an index over the collection indexes both. Rename one class, or give one a collection of its own (DocumentConventions.FindCollectionName).",
        category: "Spark",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
        LibraryNameCollisionRule, ApplicationTypeTakesLibraryNameRule, UnanchoredDeltaRule, OrphanedDeltaRule, InvalidRenameRule, SharedCollectionRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext start)
    {
        var compilation = start.Compilation;
        var isLibrary = start.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(LibraryLayersDiagnostics.AliasProperty, out var ownAlias)
            && !string.IsNullOrWhiteSpace(ownAlias);

        var renames = LibraryLayersReader.ReadRenames(compilation);
        var shipped = LibraryLayersReader.ReadShipped(compilation);
        if (shipped.Count == 0 && renames.Count == 0) return;

        var plan = LibraryLayersReader.Plan(shipped, renames);

        // SPARK061: a syntax-node action, since RegisterSymbolAction does not visit SymbolKind.Assembly (S11).
        if (renames.Count > 0)
        {
            start.RegisterSyntaxNodeAction(node => AnalyzeAttribute(node, renames, plan, isLibrary), SyntaxKind.Attribute);
        }

        // Everything else is the application's: a library composes nothing, the application referencing it does.
        if (isLibrary) return;

        var sparkContext = compilation.GetTypeByMetadataName(SparkContextFullName);
        var ravenQueryable = compilation.GetTypeByMetadataName(Naming.PlainRootClassifier.RavenQueryableMetadataName);
        var libraries = LibraryLayersReader.Apply(shipped, plan);
        var libraryFiles = ComposedModel.LibraryFiles(libraries);
        var types = LibraryTypes(libraryFiles);
        var collisions = LibraryCollisions(libraryFiles);
        var contextSeen = 0;

        if (sparkContext is not null)
        {
            start.RegisterSymbolAction(symbol =>
            {
                var type = (INamedTypeSymbol)symbol.Symbol;
                if (type.IsAbstract || type.TypeKind != TypeKind.Class || !DerivesFrom(type, sparkContext)) return;
                Interlocked.Increment(ref contextSeen);
                var location = type.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
                foreach (var collision in collisions)
                    symbol.ReportDiagnostic(CollisionDiagnostic(collision, location));
            }, SymbolKind.NamedType);

            if (ravenQueryable is not null)
            {
                start.RegisterSymbolAction(
                    symbol => AnalyzeRoot(symbol, (IPropertySymbol)symbol.Symbol, sparkContext, ravenQueryable, types, plan, compilation.AssemblyName),
                    SymbolKind.Property);
            }
        }

        start.RegisterCompilationEndAction(end =>
        {
            if (Volatile.Read(ref contextSeen) == 0)
                foreach (var collision in collisions)
                    end.ReportDiagnostic(CollisionDiagnostic(collision, Location.None));

            ReportModelFiles(end, libraryFiles);
        });
    }

    // ── SPARK061 ───────────────────────────────────────────────────────────────────────────────

    private static void AnalyzeAttribute(SyntaxNodeAnalysisContext node, IReadOnlyList<StatedRename> renames, SparkRenamePlan plan, bool isLibrary)
    {
        var syntax = (AttributeSyntax)node.Node;
        if (syntax.Parent is not AttributeListSyntax { Target.Identifier.ValueText: "assembly" }) return;

        var stated = renames.FirstOrDefault(r => r.Attribute.ApplicationSyntaxReference is { } reference
            && reference.SyntaxTree == syntax.SyntaxTree
            && reference.Span == syntax.Span);
        if (stated is null) return;

        if (isLibrary)
        {
            node.ReportDiagnostic(Diagnostic.Create(InvalidRenameRule, syntax.GetLocation(),
                $"{stated.Rename.Text}: only an application may rename a persistent-object type. A library names its own types in its model files; " +
                "an application referencing two libraries with one name renames one of them."));
            return;
        }

        foreach (var problem in plan.Problems.Where(p => ReferenceEquals(p.Rename, stated.Rename)))
            node.ReportDiagnostic(Diagnostic.Create(InvalidRenameRule, syntax.GetLocation(), problem.ToString()));
    }

    // ── SPARK057 ───────────────────────────────────────────────────────────────────────────────

    private sealed class Collision(LibraryModelFileInfo first, LibraryModelFileInfo second, string name)
    {
        public LibraryModelFileInfo First { get; } = first;
        public LibraryModelFileInfo Second { get; } = second;
        public string Name { get; } = name;
    }

    /// <summary>
    /// The library files that name a type another, unrelated library declares (names ignoring case), as
    /// <see cref="SparkModelLayers.Compose"/> refuses them: a library stacked above the declaring one states a delta.
    /// </summary>
    private static List<Collision> LibraryCollisions(IReadOnlyList<LibraryModelFileInfo> files)
    {
        var result = new List<Collision>();
        var declared = new Dictionary<string, (LibraryModelFileInfo File, string Name)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (NameOf(file.Json) is not { } name) continue;
            if (declared.TryGetValue(name, out var owner))
            {
                var related = string.Equals(owner.Name, name, StringComparison.Ordinal)
                    && (owner.File.Assembly == file.Assembly || file.DependsOn.Contains(owner.File.Assembly, StringComparer.Ordinal));
                if (!related) result.Add(new Collision(owner.File, file, name));
                continue;
            }
            declared[name] = (file, name);
        }
        return result;
    }

    private static Diagnostic CollisionDiagnostic(Collision collision, Location location)
    {
        var declaredName = collision.Second.DeclaredName ?? collision.Name;
        var properties = ImmutableDictionary<string, string?>.Empty
            .Add(AliasProperty, collision.Second.Alias)
            .Add(DeclaredNameProperty, declaredName)
            .Add(SuggestedNameProperty, Suggest(collision.Second.Alias, declaredName));
        return Diagnostic.Create(LibraryNameCollisionRule, location, properties,
            collision.First.Assembly, collision.Second.Assembly, collision.Name, collision.Second.Alias, declaredName);
    }

    // ── SPARK058 / SPARK062 ────────────────────────────────────────────────────────────────────

    private sealed class LibraryType(string name, string? clrType, string identity, string assembly)
    {
        public string Name { get; } = name;
        public string? ClrType { get; } = clrType;
        public string Identity { get; } = identity;
        public string Assembly { get; } = assembly;
    }

    private static List<LibraryType> LibraryTypes(IReadOnlyList<LibraryModelFileInfo> files)
    {
        var result = new List<LibraryType>();
        foreach (var entry in SparkModelLayers.Compose(ComposedModel.Inputs(files), []).Types)
        {
            if (entry.Name is null || entry.Library is null || entry.Identity is null) continue;
            string? clrType;
            try { clrType = SparkJson.Parse(entry.Json) is SparkJsonObject root ? SparkModelLayers.ClrTypeOf(root) : null; }
            catch (SparkJsonException) { clrType = null; }
            result.Add(new LibraryType(entry.Name, clrType, entry.Identity, entry.Library.Layer));
        }
        return result;
    }

    private static void AnalyzeRoot(
        SymbolAnalysisContext context,
        IPropertySymbol property,
        INamedTypeSymbol sparkContext,
        INamedTypeSymbol ravenQueryable,
        IReadOnlyList<LibraryType> libraryTypes,
        SparkRenamePlan plan,
        string? assemblyName)
    {
        if (property.IsStatic || !DerivesFrom(property.ContainingType, sparkContext)) return;
        if (Entity(property, ravenQueryable) is not { } entity) return;
        var location = property.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null) return;

        var clrType = MissingReferenceRootAnalyzer.MetadataName(entity);
        var own = libraryTypes.FirstOrDefault(t => string.Equals(t.ClrType, clrType, StringComparison.Ordinal));

        // SPARK058: an application class, not a library's own CLR type (SeoTester exposes the library's Organization: fine).
        if (own is null)
        {
            var name = plan.ApplicationTypes.TryGetValue(clrType, out var renamed) ? renamed : entity.Name;
            var taken = libraryTypes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (taken is not null)
            {
                var properties = ImmutableDictionary<string, string?>.Empty
                    .Add(EntityTypeProperty, clrType)
                    .Add(SuggestedNameProperty, Suggest(FirstSegment(assemblyName), entity.Name));
                context.ReportDiagnostic(Diagnostic.Create(ApplicationTypeTakesLibraryNameRule, location, properties,
                    entity.ToDisplayString(), name, taken.Identity, taken.Assembly, entity.ToDisplayString()));
            }
        }

        // SPARK062: the collection is the CLR simple name; another entity class with it shares the collection.
        var others = new List<string>();
        foreach (var library in libraryTypes)
            if (library.ClrType is { } other && other != clrType && SimpleName(other) == entity.Name)
                others.Add(other);
        foreach (var root in Roots(property.ContainingType, ravenQueryable))
        {
            var other = MissingReferenceRootAnalyzer.MetadataName(root);
            if (other != clrType && root.Name == entity.Name && !others.Contains(other))
                others.Add(other);
        }
        foreach (var other in others)
            context.ReportDiagnostic(Diagnostic.Create(SharedCollectionRule, location, clrType, other, entity.Name));
    }

    /// <summary>The entity of an <c>IRavenQueryable&lt;T&gt;</c> property, unless <c>T</c> is a view (<c>[FromIndex]</c>), which has no collection.</summary>
    private static INamedTypeSymbol? Entity(IPropertySymbol property, INamedTypeSymbol ravenQueryable)
    {
        if (property.Type is not INamedTypeSymbol { IsGenericType: true } type
            || !SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, ravenQueryable)
            || type.TypeArguments[0] is not INamedTypeSymbol entity)
            return null;
        return entity.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == FromIndexAttributeFullName) ? null : entity;
    }

    /// <summary>Every root entity of <paramref name="context"/>, own and inherited.</summary>
    private static IEnumerable<INamedTypeSymbol> Roots(INamedTypeSymbol context, INamedTypeSymbol ravenQueryable)
    {
        for (var current = context; current is not null; current = current.BaseType)
            foreach (var member in current.GetMembers().OfType<IPropertySymbol>())
                if (!member.IsStatic && Entity(member, ravenQueryable) is { } entity)
                    yield return entity;
    }

    // ── SPARK059 / SPARK060 ────────────────────────────────────────────────────────────────────

    private static void ReportModelFiles(CompilationAnalysisContext end, IReadOnlyList<LibraryModelFileInfo> libraryFiles)
    {
        var appDataDir = SparkAppDataDir.Read(end.Options.AnalyzerConfigOptionsProvider.GlobalOptions);
        var appFiles = new List<(AdditionalText File, AppModelFileInfo Info)>();
        foreach (var file in end.Options.AdditionalFiles)
        {
            if (!file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (!SparkAppDataDir.Contains(file.Path, appDataDir, "Model")) continue;
            if (end.Options.AnalyzerConfigOptionsProvider.GetOptions(file).TryGetValue(LibraryLayersGenerator.PathMetadata, out var layerPath)
                && !string.IsNullOrWhiteSpace(layerPath))
                continue;
            if (file.GetText(end.CancellationToken) is not { } text) continue;
            appFiles.Add((file, new AppModelFileInfo { Path = file.Path, Text = text.ToString() }));
        }
        if (appFiles.Count == 0 || libraryFiles.Count == 0) return;

        var composition = SparkModelLayers.Compose(ComposedModel.Inputs(libraryFiles), ComposedModel.Inputs(appFiles.Select(a => a.Info)));
        foreach (var problem in composition.Problems)
        {
            var file = appFiles.FirstOrDefault(a => a.Info.Path == problem.Layer).File;
            if (file is null) continue;
            var rule = IsOrphanedOrStale(problem.Message) ? OrphanedDeltaRule : UnanchoredDeltaRule;
            var location = Location.Create(file.Path, new TextSpan(0, 0), new LinePositionSpan(default, default));
            end.ReportDiagnostic(Diagnostic.Create(rule, location, problem.Message));
        }
    }

    /// <summary>The composer's orphaned, stale, malformed-<c>$extends</c> and id-less messages (F9); every other problem of an application file is a collision.</summary>
    internal static bool IsOrphanedOrStale(string message)
        => message.IndexOf("orphaned", StringComparison.Ordinal) >= 0
           || message.IndexOf("stale delta", StringComparison.Ordinal) >= 0
           || message.IndexOf("\"$extends\" must be", StringComparison.Ordinal) >= 0
           || message.IndexOf("states no persistentObject.id", StringComparison.Ordinal) >= 0;

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private static string? NameOf(string json)
    {
        try { return SparkJson.Parse(json) is SparkJsonObject root ? SparkModelLayers.NameOf(root) : null; }
        catch (SparkJsonException) { return null; }
    }

    private static string SimpleName(string clrType)
    {
        var cut = Math.Max(clrType.LastIndexOf('.'), clrType.LastIndexOf('+'));
        return cut < 0 ? clrType : clrType.Substring(cut + 1);
    }

    private static string? FirstSegment(string? assemblyName)
        => assemblyName is null ? null : assemblyName.Split('.')[0];

    /// <summary>A free-looking name to start from: <c>ticketing</c> + <c>Order</c> → <c>TicketingOrder</c>.</summary>
    internal static string Suggest(string? prefix, string name)
    {
        var builder = new System.Text.StringBuilder();
        var upper = true;
        foreach (var c in prefix ?? string.Empty)
        {
            if (!char.IsLetterOrDigit(c)) { upper = true; continue; }
            builder.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        if (builder.Length > 0 && char.IsDigit(builder[0])) builder.Insert(0, '_');
        var suggestion = builder.Append(name).ToString();
        return string.Equals(suggestion, name, StringComparison.Ordinal) ? "My" + name : suggestion;
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, baseType)) return true;
        return false;
    }
}
