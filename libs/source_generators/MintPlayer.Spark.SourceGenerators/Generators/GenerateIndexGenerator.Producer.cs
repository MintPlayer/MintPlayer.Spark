using Microsoft.CodeAnalysis;
using MintPlayer.Spark.SourceGenerators.Diagnostics;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.Spark.SourceGenerators.Naming;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Emits the index / index-entity pairs described by <see cref="GeneratedIndexInfo"/>.
/// <para>
/// Both types are <c>partial</c> so a developer can extend either by hand, and the index constructor ends
/// with <c>OnInitialize()</c> — the sanctioned seam for extra index configuration. That pairing is the
/// alternative to the stringly-typed expression escape hatches the reference design uses, which inject raw
/// C# referencing lambda variable names the author has to guess.
/// </para>
/// </summary>
public class GenerateIndexProducer : Producer, IDiagnosticReporter
{
    private const string RavenIndexes = "global::Raven.Client.Documents.Indexes";
    private const string SparkAbstractions = "global::MintPlayer.Spark.Abstractions";

    private readonly IEnumerable<GeneratedIndexInfo> entities;
    private readonly bool knowsSpark;
    private readonly List<string> languages;

    public GenerateIndexProducer(
        IEnumerable<GeneratedIndexInfo> entities,
        bool knowsSpark,
        List<string> languages,
        string rootNamespace)
        : base(rootNamespace, "SparkGeneratedIndexes.g.cs")
    {
        this.entities = entities;
        this.knowsSpark = knowsSpark;
        this.languages = languages;
    }

    /// <summary>
    /// The fields actually emitted for an entity: its properties, with every <c>TranslatedString</c> expanded
    /// into one field per language.
    /// <para>
    /// A <c>TranslatedString</c> is a dictionary, and RavenDB cannot usefully sort or search one. It persists
    /// nested — <c>Description.Translations.nl</c> — so a per-language field maps
    /// <c>car.Description!.Translations["nl"]</c>, which RavenDB evaluates natively against the stored
    /// document. A missing key or a null property both index to null with no error.
    /// </para>
    /// <para>
    /// The whole-object field is replaced rather than kept alongside: emitting a <c>string?</c> named
    /// <c>Description</c> beside the entity's <c>TranslatedString Description</c> would be a type mismatch the
    /// model merge rejects, and keeping both doubles every translated field for no query benefit.
    /// </para>
    /// </summary>
    private IEnumerable<IndexPropertyInfo> Fields(GeneratedIndexInfo info)
    {
        foreach (var property in info.Properties)
        {
            if (!property.IsTranslated)
            {
                yield return property;
                continue;
            }

            foreach (var language in languages)
            {
                var languageField = new IndexPropertyInfo
                {
                    Name = IndexNaming.LanguageField(property.Name, language),
                    TypeDisplay = "string?",
                    NeedsDefaultInitializer = false,
                    MapExpression = $"{info.ItemVariable}.{property.Name}!.Translations[{Quote(language)}]",
                    FieldIndexing = property.IsSearchable ? "Search" : null,
                    Attributes = property.Attributes,
                };
                yield return languageField;

                if (property.IsSearchable)
                    yield return SortCompanionOf(languageField);
            }
        }
    }

    /// <summary>
    /// The companion for a per-language field. Same shape as the entity-side companion: identical map
    /// expression, no <c>FieldIndexing</c>, and <c>[IgnoreProperty]</c> so it stays out of the model.
    /// </summary>
    private static IndexPropertyInfo SortCompanionOf(IndexPropertyInfo field) => new()
    {
        Name = IndexNaming.SortCompanion(field.Name),
        TypeDisplay = field.TypeDisplay,
        MapExpression = field.MapExpression,
        FieldIndexing = null,
        IsSortCompanion = true,
    };

    /// <summary>
    /// Diagnostics for the same entity set this producer emits from.
    /// <para>Owned here rather than in the generator so the emitted source and the reported problems are
    /// projected from one model. The reference design derived its outputs in two independent traversals,
    /// which is where "the index and the context disagree" bugs come from.</para>
    /// </summary>
    public IEnumerable<Diagnostic> GetDiagnostics(Compilation compilation)
    {
        if (!knowsSpark) yield break;

        var byIndexName = new Dictionary<string, GeneratedIndexInfo>(System.StringComparer.Ordinal);

        foreach (var entity in Ordered())
        {
            foreach (var invalid in entity.InvalidSearchProperties)
            {
                yield return GenerateIndexDiagnostics.SearchOnUnsupportedType.Create(
                    invalid.Location.ToLocation(compilation), invalid.PropertyName, invalid.TypeDisplay);
            }

            foreach (var ignored in entity.IgnoredSearchProperties)
            {
                yield return GenerateIndexDiagnostics.SearchOnIgnoredProperty.Create(
                    ignored.Location.ToLocation(compilation), ignored.PropertyName);
            }

            foreach (var dropped in entity.UnrenderableAttributes)
            {
                yield return GenerateIndexDiagnostics.AttributeNotCopied.Create(
                    dropped.Location.ToLocation(compilation), dropped.TypeDisplay, dropped.PropertyName);
            }

            foreach (var complex in entity.ComplexProperties)
            {
                yield return GenerateIndexDiagnostics.ComplexPropertyStoredNotIndexed.Create(
                    complex.Location.ToLocation(compilation), complex.PropertyName, complex.TypeDisplay);
            }

            if (entity.Properties.Count == 0)
            {
                yield return GenerateIndexDiagnostics.NoIndexableProperties.Create(
                    entity.Location.ToLocation(compilation), entity.EntityFullName);
                continue;
            }

            if (byIndexName.TryGetValue(entity.IndexName, out var winner))
            {
                yield return GenerateIndexDiagnostics.DuplicateIndexName.Create(
                    entity.Location.ToLocation(compilation),
                    winner.EntityFullName, entity.EntityFullName, entity.IndexName);
                continue;
            }

            byIndexName.Add(entity.IndexName, entity);
        }
    }

    /// <summary>
    /// Entities actually emitted: those with something to index, one per index name. The first declaration
    /// wins so the output stays deterministic; the loser is reported by <see cref="GetDiagnostics"/>.
    /// </summary>
    private List<GeneratedIndexInfo> Emitted()
    {
        var seenIndexNames = new HashSet<string>(System.StringComparer.Ordinal);
        var result = new List<GeneratedIndexInfo>();
        foreach (var entity in Ordered())
        {
            if (entity.Properties.Count == 0) continue;
            if (!seenIndexNames.Add(entity.IndexName)) continue;
            result.Add(entity);
        }

        return result;
    }

    private IEnumerable<GeneratedIndexInfo> Ordered()
        => entities.OrderBy(e => e.EntityFullName, System.StringComparer.Ordinal);

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var list = Emitted();

        if (!knowsSpark || list.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("#nullable enable");
        writer.WriteLine();

        // Required by the query-syntax map expression below. Everything else is global::-qualified, but
        // `from x in xs select ...` needs System.Linq in scope and cannot be qualified away.
        writer.WriteLine("using System.Linq;");
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {IndexNamespace}"))
        {
            for (var i = 0; i < list.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (i > 0) writer.WriteLine();
                WriteIndexEntity(writer, list[i]);
                writer.WriteLine();
                WriteIndex(writer, list[i], cancellationToken);
            }
        }
    }

    /// <summary>Namespace for both generated types — the app's, never the entity's.</summary>
    private string IndexNamespace => $"{RootNamespace}.Indexes";

    private void WriteIndexEntity(IndentedTextWriter writer, GeneratedIndexInfo info)
    {
        writer.WriteLine($"[{SparkAbstractions}.FromIndexAttribute(typeof({info.IndexName}))]");
        using (writer.OpenBlock($"public partial class {info.IndexEntityName}"))
        {
            // Declared but never assigned in the map: RavenDB supplies the document id for an entity index.
            writer.WriteLine("public string? Id { get; set; }");

            foreach (var property in Fields(info))
            {
                foreach (var attribute in Attributes(property))
                    writer.WriteLine(attribute);

                var initializer = property.NeedsDefaultInitializer ? " = default!;" : string.Empty;
                writer.WriteLine($"public {property.TypeDisplay} {property.Name} {{ get; set; }}{initializer}");
            }
        }
    }

    private void WriteIndex(IndentedTextWriter writer, GeneratedIndexInfo info, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(info.Description))
            writer.WriteLine($"[global::System.ComponentModel.Description({Quote(info.Description!)})]");

        using (writer.OpenBlock($"public partial class {info.IndexName} : {RavenIndexes}.AbstractIndexCreationTask<{info.EntityFullName}>"))
        {
            using (writer.OpenBlock($"public {info.IndexName}()"))
            {
                writer.WriteLine($"Map = {info.CollectionVariable} => from {info.ItemVariable} in {info.CollectionVariable}");
                writer.Indent++;
                writer.WriteLine($"select new {info.IndexEntityName}()");
                writer.WriteLine("{");
                writer.Indent++;
                foreach (var property in Fields(info))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteLine($"{property.Name} = {property.MapExpression},");
                }
                writer.Indent--;
                writer.WriteLine("};");
                writer.Indent--;

                // Only fields that need non-default indexing are declared. A sort companion is deliberately
                // absent from this list: leaving it undeclared is what keeps it a single un-tokenized term
                // and therefore sortable.
                foreach (var property in Fields(info).Where(p => p.FieldIndexing is not null))
                {
                    writer.WriteLine(
                        $"Index(nameof({info.IndexEntityName}.{property.Name}), {RavenIndexes}.FieldIndexing.{property.FieldIndexing});");
                }

                // Mandatory, not conventional. Without it a projection-only field comes back null through
                // ProjectInto while the index itself is provably correct -- no error, no index fault, just
                // empty values. It is the likeliest way a generated index appears broken.
                writer.WriteLine($"StoreAllFields({RavenIndexes}.FieldStorage.Yes);");
                writer.WriteLine("OnInitialize();");
            }

            writer.WriteLine();
            writer.WriteLine("/// <summary>Called at the end of the generated constructor. Implement in a hand-written partial to add index configuration.</summary>");
            writer.WriteLine("partial void OnInitialize();");
        }
    }

    /// <summary>
    /// Attribute lines for one field declaration. A sort companion always carries
    /// <c>[IgnoreProperty]</c>: it is hidden from the Spark model — no model attribute, no label, no
    /// <c>AttributeNames</c> constant — while staying an ordinary property that LINQ can filter on.
    /// </summary>
    private static IEnumerable<string> Attributes(IndexPropertyInfo property)
    {
        if (property.IsSortCompanion)
            yield return $"[{SparkAbstractions}.IgnorePropertyAttribute]";

        foreach (var attribute in property.Attributes)
            yield return attribute;
    }

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
