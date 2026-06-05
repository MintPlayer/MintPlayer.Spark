using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class PersistentObjectNamesProducer : Producer
{
    private readonly IEnumerable<PersistentObjectInfo> persistentObjects;
    private readonly bool knowsSpark;

    public PersistentObjectNamesProducer(
        IEnumerable<PersistentObjectInfo> persistentObjects,
        bool knowsSpark,
        string rootNamespace)
        : base(rootNamespace, "PersistentObjectNames.g.cs")
    {
        this.persistentObjects = persistentObjects;
        this.knowsSpark = knowsSpark;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var list = persistentObjects
            .GroupBy(p => p.EntityName, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(p => p.EntityName, StringComparer.Ordinal)
            .ToList();

        if (!knowsSpark || list.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("public static class PersistentObjectNames"))
            {
                foreach (var po in list)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteLine($"public const string {po.EntityName} = \"{po.EntityName}\";");
                }
            }
        }
    }
}
