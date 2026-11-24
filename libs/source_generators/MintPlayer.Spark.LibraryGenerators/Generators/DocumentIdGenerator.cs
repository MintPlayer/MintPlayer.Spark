using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.SourceGenerators.Tools;
using MintPlayer.Spark.LibraryGenerators.Models;
using System.CodeDom.Compiler;
using System.Collections.Immutable;

namespace MintPlayer.Spark.LibraryGenerators.Generators;

/// <summary>
/// Gives every entity whose <c>Id</c> is an <c>Id&lt;Self&gt;</c> the hidden string member RavenDB uses as its
/// identity property (#536 D23 rule 6, spike S-ID1):
/// <code>
/// private string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = Id&lt;Person&gt;.FromTrusted(value); }
/// </code>
/// RavenDB refuses a non-string identity member, so the typed <c>Id</c> can't be the identity itself. The member is
/// private: RavenDB finds it through <c>FindIdentityProperty</c>, the serializers skip it, and no app code sees it.
/// <c>FromTrusted</c> lowers what RavenDB hands back, so a legacy mixed-case id reads as its lowercase form (D7).
/// Providers that map the typed id directly (EF Core) ignore the member.
/// </summary>
[Generator(LanguageNames.CSharp)]
public class DocumentIdGenerator : IncrementalGenerator
{
    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        var entitiesProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => node is PropertyDeclarationSyntax { Identifier.ValueText: "Id", Type: GenericNameSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax },
                transform: static (ctx, ct) => Describe(ctx, ct))
            .Where(static x => x is not null)
            .Select(static (x, ct) => x!)
            .Collect()
            .Select(static (found, ct) => Distinct(found));

        var sourceProvider = entitiesProvider
            .Join(settingsProvider)
            .Select(static Producer (p, ct) => new DocumentIdProducer(p.Item1, p.Item2.RootNamespace ?? "GeneratedCode"));

        var diagnosticsProvider = entitiesProvider
            .Select(static IDiagnosticReporter (entities, ct) => new DocumentIdReporter(entities));

        context.ProduceCode(sourceProvider);
        context.ReportDiagnostics(diagnosticsProvider);
    }

    private static DocumentIdInfo? Describe(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not IPropertySymbol { IsStatic: false } property)
            return null;
        if (property.Type is not INamedTypeSymbol { IsGenericType: true } idType
            || idType.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != "global::MintPlayer.Spark.Abstractions.Id<T>"
            || !SymbolEqualityComparer.Default.Equals(idType.TypeArguments[0], property.ContainingType))
            return null;

        var type = property.ContainingType;
        return new DocumentIdInfo
        {
            FullyQualifiedName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Name = type.Name,
            Header = PartialTypeHeader.For(type),
            PathSpec = type.GetPathSpec(ct),
            IsPartial = PartialTypeHeader.IsPartialAllTheWayUp(type, ct),
            Location = property.Locations.FirstOrDefault()?.AsKey(),
        };
    }

    private static ImmutableArray<DocumentIdInfo> Distinct(IEnumerable<DocumentIdInfo> found)
    {
        var seen = new Dictionary<string, DocumentIdInfo>(StringComparer.Ordinal);
        foreach (var info in found)
            if (!seen.ContainsKey(info.FullyQualifiedName))
                seen.Add(info.FullyQualifiedName, info);
        return [.. seen.Values.OrderBy(static x => x.FullyQualifiedName, StringComparer.Ordinal)];
    }
}

/// <summary>Emits the <c>__DocumentId</c> member of every partial entity <see cref="DocumentIdGenerator"/> found.</summary>
public class DocumentIdProducer : Producer
{
    private readonly ImmutableArray<DocumentIdInfo> entities;

    public DocumentIdProducer(ImmutableArray<DocumentIdInfo> entities, string rootNamespace)
        : base(rootNamespace, "SparkDocumentIds.g.cs")
    {
        this.entities = entities;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var emitted = entities.Where(static e => e.IsPartial).ToArray();
        if (emitted.Length == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();
        // The identity is string?: a project without <Nullable>enable</Nullable> warned CS8669 on it (Apps, #536 M2c).
        writer.WriteLine("#nullable enable");
        writer.WriteLine();

        foreach (var group in emitted.GroupBy(static e => e.PathSpec?.ContainingNamespace ?? string.Empty))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IDisposable? namespaceBlock = string.IsNullOrEmpty(group.Key) ? null : writer.OpenBlock($"namespace {group.Key}");

            foreach (var entity in group)
            {
                using var parents = writer.OpenPathSpec(entity.PathSpec);
                using (writer.OpenBlock(entity.Header))
                {
                    writer.WriteLine("/// <summary>The document id RavenDB reads and writes: <c>Id</c> as its canonical string (#536 D23 rule 6).</summary>");
                    writer.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
                    writer.WriteLine("[global::System.Text.Json.Serialization.JsonIgnore]");
                    writer.WriteLine($"private string? __DocumentId {{ get => Id.IsEmpty ? null : Id.Value; set => Id = global::MintPlayer.Spark.Abstractions.Id<{entity.FullyQualifiedName}>.FromTrusted(value); }}");
                }
            }

            namespaceBlock?.Dispose();
        }
    }
}

/// <summary>Reports the entities with an <c>Id&lt;Self&gt;</c> that are not partial (SPARK064).</summary>
public class DocumentIdReporter : IDiagnosticReporter
{
    internal static readonly DiagnosticDescriptor EntityMustBePartialRule = new(
        id: "SPARK064",
        title: "An entity with an Id<T> must be partial",
        messageFormat: "'{0}' declares 'Id<{0}> Id', so it needs a generated document-id member — declare it 'partial'",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "RavenDB's identity member must be a string, so Spark generates a hidden string member that mirrors the typed Id (#536 D23 rule 6). Without it RavenDB can't store or load the entity.");

    private readonly ImmutableArray<DocumentIdInfo> entities;

    public DocumentIdReporter(ImmutableArray<DocumentIdInfo> entities)
    {
        this.entities = entities;
    }

    public IEnumerable<Diagnostic> GetDiagnostics(Compilation compilation)
        => entities
            .Where(static e => !e.IsPartial)
            .Select(e => EntityMustBePartialRule.Create(e.Location?.ToLocation(compilation), e.Name));
}
