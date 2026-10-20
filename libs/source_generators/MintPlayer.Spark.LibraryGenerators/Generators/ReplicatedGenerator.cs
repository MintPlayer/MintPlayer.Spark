using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.LibraryGenerators.Models;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using System.Collections.Immutable;
using MintPlayer.SourceGenerators.Tools;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

/// <summary>
/// Generates the replication constants of every <c>[Replicated]</c> class — manifest, ETL script and hash —
/// and lists the classes in one <c>[assembly: SparkReplicaTypes(...)]</c> (warehouse WD13/WD14).
/// </summary>
/// <remarks>
/// The replica class is the single declaration of the replicated shape: the manifest is read off its
/// properties, and the script is rendered by the same <see cref="ReplicaScriptRenderer"/> source file the
/// owner uses at runtime (linked in), so the owner can re-render a posted manifest byte for byte.
/// <para>
/// A class with any problem gets nothing generated and is left out of the assembly list; the problems are
/// reported as SPARK065–SPARK068. Without the constants the runtime has no script, so these are errors, not
/// a runtime fallback.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public class ReplicatedGenerator : IncrementalGenerator
{
    private const string ReplicatedAttribute = "MintPlayer.Spark.Replication.Abstractions.ReplicatedAttribute";
    private const string ReplicatedFromAttribute = "MintPlayer.Spark.Replication.Abstractions.ReplicatedFromAttribute";
    private const string IgnorePropertyAttribute = "MintPlayer.Spark.Abstractions.IgnorePropertyAttribute";

    /// <summary>The members the producer emits; a hand-written one of the same name is SPARK068.</summary>
    internal static readonly string[] GeneratedMembers =
    [
        "ReplicationSourceCollection",
        "ReplicationTargetCollection",
        "ReplicationSourceModule",
        "ReplicationWriteBack",
        "ReplicationManifest",
        "ReplicationScript",
        "ReplicationHash",
    ];

    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        var replicasProvider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ReplicatedAttribute,
                predicate: static (node, ct) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => Describe(ctx, ct))
            .Where(static x => x is not null)
            .Select(static (x, ct) => x!)
            .Collect()
            .Select(static (found, ct) => Distinct(found));

        var membersProvider = replicasProvider
            .Join(settingsProvider)
            .Select(static Producer (p, ct) => new ReplicatedMembersProducer(p.Item1, p.Item2.RootNamespace ?? "GeneratedCode"));

        var typesProvider = replicasProvider
            .Join(settingsProvider)
            .Select(static Producer (p, ct) => new ReplicaTypesProducer(p.Item1, p.Item2.RootNamespace ?? "GeneratedCode"));

        var diagnosticsProvider = replicasProvider
            .Select(static IDiagnosticReporter (replicas, ct) => new ReplicatedReporter(replicas));

        context.ProduceCode(membersProvider, typesProvider);

        // Not optional: the producers skip every type with a problem, and only the reporter says why.
        context.ReportDiagnostics(diagnosticsProvider);
    }

    private static ReplicaInfo? Describe(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type || ctx.Attributes.IsEmpty)
            return null;

        var typeLocation = (ctx.TargetNode as TypeDeclarationSyntax)?.Identifier.GetLocation().AsKey();
        var problems = new List<ReplicaProblem>();

        void Report(string id, LocationKey? location, params string[] arguments)
            => problems.Add(new ReplicaProblem
            {
                Id = id,
                Arguments = string.Join(ReplicatedReporter.Separator, arguments),
                Location = location ?? typeLocation,
            });

        // SPARK065: the generator reopens the type and every type containing it.
        var notPartial = Enumerable.Repeat(type, 1).Concat(ContainingTypes(type))
            .FirstOrDefault(t => !IsPartialFromHere(t, ct));
        if (notPartial is not null)
        {
            Report(ReplicatedReporter.MustBePartialId, null, ReferenceEquals(notPartial, type)
                ? "it is not declared 'partial'"
                : $"its containing type '{notPartial.Name}' is not declared 'partial'");
        }
        if (Enumerable.Repeat(type, 1).Concat(ContainingTypes(type)).Any(static t => t.IsGenericType))
            Report(ReplicatedReporter.MustBePartialId, null,
                "it is generic or nested in a generic type, and a replica must be one concrete document shape");

        // SPARK068: a hand-written member named like a generated one.
        foreach (var name in GeneratedMembers)
        {
            foreach (var member in type.GetMembers(name))
                Report(ReplicatedReporter.CollidingMemberId, member.Locations.FirstOrDefault()?.AsKey(), name);
        }

        // The attribute's arguments.
        var attribute = ctx.Attributes[0];
        string? sourceCollection = null, sourceModule = null, targetCollection = null;
        ITypeSymbol? originalType = null;
        var writeBack = false;
        foreach (var argument in attribute.NamedArguments)
        {
            switch (argument.Key)
            {
                case "SourceCollection": sourceCollection = argument.Value.Value as string; break;
                case "SourceModule": sourceModule = argument.Value.Value as string; break;
                case "TargetCollection": targetCollection = argument.Value.Value as string; break;
                case "OriginalType": originalType = argument.Value.Value as ITypeSymbol; break;
                case "WriteBack": writeBack = argument.Value.Value is true; break;
            }
        }

        targetCollection ??= ReplicaCollectionNames.Pluralize(type.Name);

        if (string.IsNullOrEmpty(sourceCollection))
            Report(ReplicatedReporter.InvalidDeclarationId, null, "SourceCollection is missing or empty");
        else if (!ReplicaScriptRenderer.IsIdentifier(sourceCollection))
            Report(ReplicatedReporter.InvalidDeclarationId, null, $"the SourceCollection '{sourceCollection}' is not a plain identifier");
        if (!ReplicaScriptRenderer.IsIdentifier(targetCollection))
            Report(ReplicatedReporter.InvalidDeclarationId, null, $"the TargetCollection '{targetCollection}' is not a plain identifier");
        if (sourceModule is not null && !IsModuleName(sourceModule))
            Report(ReplicatedReporter.InvalidDeclarationId, null, $"the SourceModule '{sourceModule}' does not match ^[A-Za-z0-9_.-]+$");

        // The mapped fields.
        var fields = new List<ReplicaField>();
        foreach (var property in MappedProperties(type))
        {
            ct.ThrowIfCancellationRequested();

            var location = property.Locations.FirstOrDefault()?.AsKey();
            var from = property.GetAttributes()
                .FirstOrDefault(static a => a.AttributeClass?.ToDisplayString() == ReplicatedFromAttribute);
            var pathText = from is { ConstructorArguments.Length: > 0 }
                ? from.ConstructorArguments[0].Value as string ?? string.Empty
                : property.Name;
            var path = pathText.Split('.');

            if (!ReplicaScriptRenderer.IsIdentifier(property.Name))
            {
                Report(ReplicatedReporter.InvalidDeclarationId, location, $"the property '{property.Name}' is not a plain identifier");
                continue;
            }
            if (path.Any(static segment => !ReplicaScriptRenderer.IsIdentifier(segment)))
            {
                Report(ReplicatedReporter.InvalidDeclarationId, location,
                    $"the source path '{pathText}' of '{property.Name}' has a segment that is not a plain identifier");
                continue;
            }
            if (originalType is not null && !PathExists(originalType, path))
            {
                Report(ReplicatedReporter.MissingPathId, location, pathText, property.Name,
                    originalType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                continue;
            }

            fields.Add(new ReplicaField(property.Name, path));
        }

        if (fields.Count == 0 && !problems.Any(static p => p.Id == ReplicatedReporter.InvalidDeclarationId || p.Id == ReplicatedReporter.MissingPathId))
            Report(ReplicatedReporter.InvalidDeclarationId, null,
                "it has no property to replicate (a public instance property with a public setter or init accessor, other than Id)");

        var info = new ReplicaInfo
        {
            FullyQualifiedName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Name = type.Name,
            Header = PartialTypeHeader.For(type),
            PathSpec = type.GetPathSpec(ct),
            SourceCollection = sourceCollection ?? string.Empty,
            TargetCollection = targetCollection,
            SourceModule = sourceModule,
            WriteBack = writeBack,
            Problems = problems,
        };

        if (problems.Count > 0)
            return info;

        var manifest = new ReplicaManifest(sourceCollection!, targetCollection, fields);
        if (ReplicaScriptRenderer.Validate(manifest) is { } invalid)
        {
            // The checks above cover every rule Validate has (duplicate targets cannot occur: property
            // names are unique), so this is a backstop that keeps a renderer change from throwing here.
            Report(ReplicatedReporter.InvalidDeclarationId, null, invalid);
            return info;
        }

        info.Manifest = ReplicaScriptRenderer.ToJson(manifest);
        info.Script = ReplicaScriptRenderer.RenderScript(manifest);
        info.Hash = ReplicaScriptRenderer.Hash(info.Manifest, info.Script);
        return info;
    }

    /// <summary>
    /// The replicated properties, base type's first, each in declaration order: public, instance, not an
    /// indexer, with a public setter or <c>init</c> accessor, not <c>Id</c> and not <c>[IgnoreProperty]</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="INamedTypeSymbol.GetMembers()"/> lists source members in declaration order, partial parts
    /// in syntax-tree order, so the manifest (and therefore the hash) is stable across builds. A property a
    /// derived type redeclares keeps its base type's position.
    /// </remarks>
    private static IEnumerable<IPropertySymbol> MappedProperties(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            chain.Insert(0, t);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in chain)
        {
            foreach (var property in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public)
                    continue;
                if (property.SetMethod is not { DeclaredAccessibility: Accessibility.Public })
                    continue;
                if (property.Name == "Id")
                    continue;
                if (property.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == IgnorePropertyAttribute))
                    continue;
                if (seen.Add(property.Name))
                    yield return property;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> names a property chain on <paramref name="type"/>: each segment a
    /// property of the previous one's type (declared or inherited), <see cref="Nullable{T}"/> unwrapped.
    /// </summary>
    private static bool PathExists(ITypeSymbol type, IReadOnlyList<string> path)
    {
        var current = type;
        foreach (var segment in path)
        {
            if (current is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
                current = nullable.TypeArguments[0];

            IPropertySymbol? found = null;
            for (var t = current; t is not null && found is null; t = t.BaseType)
                found = t.GetMembers(segment).OfType<IPropertySymbol>().FirstOrDefault(static p => !p.IsStatic);

            if (found is null && current.TypeKind == TypeKind.Interface)
            {
                found = current.AllInterfaces
                    .SelectMany(i => i.GetMembers(segment).OfType<IPropertySymbol>())
                    .FirstOrDefault();
            }

            if (found is null)
                return false;
            current = found.Type;
        }

        return true;
    }

    private static IEnumerable<INamedTypeSymbol> ContainingTypes(INamedTypeSymbol type)
    {
        for (var t = type.ContainingType; t is not null; t = t.ContainingType)
            yield return t;
    }

    /// <summary>Whether every declaration of <paramref name="type"/> itself (not its containers) is partial.</summary>
    private static bool IsPartialFromHere(INamedTypeSymbol type, CancellationToken ct)
    {
        var declarations = type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(ct))
            .OfType<TypeDeclarationSyntax>()
            .ToList();
        return declarations.Count > 0
            && declarations.All(static d => d.Modifiers.Any(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword));
    }

    private static bool IsModuleName(string value)
        => value.Length > 0 && value.All(static c =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-');

    /// <summary>One entry per type, ordered by full name, so the emitted files are stable across runs.</summary>
    private static ImmutableArray<ReplicaInfo> Distinct(IEnumerable<ReplicaInfo> found)
    {
        var seen = new Dictionary<string, ReplicaInfo>(StringComparer.Ordinal);
        foreach (var info in found)
        {
            if (!seen.ContainsKey(info.FullyQualifiedName))
                seen.Add(info.FullyQualifiedName, info);
        }

        return [.. seen.Values.OrderBy(static x => x.FullyQualifiedName, StringComparer.Ordinal)];
    }
}
