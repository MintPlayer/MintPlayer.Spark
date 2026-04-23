using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// Emits <c>PersistentObjectIds</c>: nested static classes per database schema,
/// each holding <c>const string</c> Guid values for every entity declared under
/// that schema. Source of truth is <c>App_Data/Model/*.json</c> files surfaced
/// via <c>&lt;AdditionalFiles Include="App_Data\Model\*.json" /&gt;</c> in the
/// consumer's csproj.
/// </summary>
/// <example>
/// <code>
/// // Generated output
/// public static class PersistentObjectIds
/// {
///     public static class Default
///     {
///         public const string Car    = "27768be5-2ff5-4782-8b22-c0e8d163050e";
///         public const string Person = "...";
///     }
///
///     public static class Audit
///     {
///         public const string AuditLog = "...";
///     }
/// }
///
/// // Consumer
/// var po = manager.NewPersistentObject(new Guid(PersistentObjectIds.Default.Car));
/// </code>
/// </example>
public class PersistentObjectIdsProducer : Producer
{
    private readonly IReadOnlyList<PersistentObjectIdInfo> ids;
    private readonly bool knowsSpark;

    public PersistentObjectIdsProducer(
        IEnumerable<PersistentObjectIdInfo> ids,
        bool knowsSpark,
        string rootNamespace)
        : base(rootNamespace, "PersistentObjectIds.g.cs")
    {
        this.ids = ids.ToList();
        this.knowsSpark = knowsSpark;
    }

    protected override void ProduceSource(IndentedTextWriter writer, System.Threading.CancellationToken cancellationToken)
    {
        if (!knowsSpark || ids.Count == 0)
            return;

        // Deduplicate by (Schema, Name) — first write wins — then group per schema.
        var bySchema = ids
            .GroupBy(i => (i.Schema, i.Name))
            .Select(g => g.First())
            .GroupBy(i => i.Schema, System.StringComparer.Ordinal)
            .OrderBy(g => g.Key, System.StringComparer.Ordinal)
            .ToList();

        writer.WriteLine(Header);
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("public static class PersistentObjectIds"))
            {
                for (var i = 0; i < bySchema.Count; i++)
                {
                    var schema = bySchema[i];
                    if (i > 0) writer.WriteLine();

                    using (writer.OpenBlock($"public static class {schema.Key}"))
                    {
                        var entries = schema
                            .OrderBy(e => e.Name, System.StringComparer.Ordinal)
                            .ToList();

                        foreach (var entry in entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            writer.WriteLine($"public const string {entry.Name} = \"{entry.Id}\";");
                        }
                    }
                }
            }
        }
    }
}
