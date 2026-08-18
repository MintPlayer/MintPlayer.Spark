using MintPlayer.Spark.SourceGenerators.Models;
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
public class GenerateIndexProducer : Producer
{
    private const string RavenIndexes = "global::Raven.Client.Documents.Indexes";
    private const string SparkAbstractions = "global::MintPlayer.Spark.Abstractions";

    private readonly IEnumerable<GeneratedIndexInfo> entities;
    private readonly bool knowsSpark;

    public GenerateIndexProducer(IEnumerable<GeneratedIndexInfo> entities, bool knowsSpark, string rootNamespace)
        : base(rootNamespace, "SparkGeneratedIndexes.g.cs")
    {
        this.entities = entities;
        this.knowsSpark = knowsSpark;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var list = entities.ToList();

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
        writer.WriteLine($"[{SparkAbstractions}.FromIndex(typeof({info.IndexName}))]");
        using (writer.OpenBlock($"public partial class {info.IndexEntityName}"))
        {
            // Declared but never assigned in the map: RavenDB supplies the document id for an entity index.
            writer.WriteLine("public string? Id { get; set; }");

            foreach (var property in info.Properties)
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
                foreach (var property in info.Properties)
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
                foreach (var property in info.Properties.Where(p => p.FieldIndexing is not null))
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
            yield return $"[{SparkAbstractions}.IgnoreProperty]";

        foreach (var attribute in property.Attributes)
            yield return attribute;
    }

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
