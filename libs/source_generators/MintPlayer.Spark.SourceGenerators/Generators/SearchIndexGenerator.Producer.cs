using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>Writes <c>Spark_Search</c> (index name <c>Spark/Search</c>) for <see cref="SearchIndexGenerator"/>.</summary>
public class SearchIndexProducer : Producer
{
    private readonly IReadOnlyList<SearchMapInfo> maps;

    public SearchIndexProducer(IReadOnlyList<SearchMapInfo> maps, string rootNamespace)
        : base(rootNamespace, "Spark_Search.g.cs")
    {
        this.maps = maps;
    }

    protected override void ProduceSource(IndentedTextWriter writer, System.Threading.CancellationToken cancellationToken)
    {
        if (maps.Count == 0)
            return;

        const string entry = "global::MintPlayer.Spark.Abstractions.Search.SparkSearchEntry";
        const string index = "global::MintPlayer.Spark.Abstractions.Search.SparkSearchIndex";

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("using System.Linq;");
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}.Indexes"))
        {
            writer.WriteLine("/// <summary>");
            writer.WriteLine("/// The searchable types of this application (docs/spark_search_PRD.md D5), generated from the model's");
            writer.WriteLine("/// <c>search</c> blocks. Soft-deleted rows are mapped with <c>IsDeleted</c>, so a search engine syncing");
            writer.WriteLine("/// from this index sees them leave. Lucene, because Corax has no fuzzy search.");
            writer.WriteLine("/// </summary>");
            writer.WriteLine("[global::System.CodeDom.Compiler.GeneratedCode(\"MintPlayer.Spark.SourceGenerators\", \"1.0\")]");
            using (writer.OpenBlock($"public sealed class Spark_Search : global::MintPlayer.Spark.SparkMultiMapIndexCreationTask<{entry}>"))
            {
                using (writer.OpenBlock("public Spark_Search()"))
                {
                    foreach (var map in maps)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var text = string.Join(" + \" \" + ", map.Fields.Select(f => f.IsCollection
                            ? $"(d.{f.Name} == null ? \"\" : string.Join(\" \", d.{f.Name}))"
                            : $"(d.{f.Name} ?? \"\")"));
                        var deleted = map.HasIsDeleted ? "d.IsDeleted == true" : "false";

                        writer.WriteLine($"AddMap<{map.EntityFullName}>(docs => from d in docs");
                        writer.Indent++;
                        writer.WriteLine($"let text = {text}");
                        writer.WriteLine($"select new {entry}");
                        writer.WriteLine("{");
                        writer.Indent++;
                        writer.WriteLine("Id = d.Id,");
                        writer.WriteLine($"Type = {SymbolDisplay.FormatLiteral(map.EntityName, true)},");
                        writer.WriteLine("Text = text,");
                        writer.WriteLine("Terms = new[] { text },");
                        writer.WriteLine($"IsDeleted = {deleted},");
                        writer.WriteLine("LastModified = MetadataFor(d).Value<global::System.DateTime>(\"@last-modified\"),");
                        writer.Indent--;
                        writer.WriteLine("});");
                        writer.Indent--;
                    }

                    writer.WriteLine();
                    writer.WriteLine($"Index({index}.Text, global::Raven.Client.Documents.Indexes.FieldIndexing.Search);");
                    writer.WriteLine($"Index({index}.Terms, global::Raven.Client.Documents.Indexes.FieldIndexing.Search);");
                    // Stored for the projections: the provider reads Type, an engine's sync reads them all. The source
                    // document has none of these fields, so a projection can only come from the index.
                    writer.WriteLine($"Store({index}.Type, global::Raven.Client.Documents.Indexes.FieldStorage.Yes);");
                    writer.WriteLine($"Store({index}.Text, global::Raven.Client.Documents.Indexes.FieldStorage.Yes);");
                    writer.WriteLine($"Store({index}.IsDeleted, global::Raven.Client.Documents.Indexes.FieldStorage.Yes);");
                    writer.WriteLine($"Store({index}.LastModified, global::Raven.Client.Documents.Indexes.FieldStorage.Yes);");
                    writer.WriteLine("Configuration[\"Indexing.Static.SearchEngineType\"] = \"Lucene\";");
                }
            }
        }
    }
}
