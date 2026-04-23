using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class AttributeNamesProducer : Producer
{
    private readonly IEnumerable<PersistentObjectInfo> persistentObjects;
    private readonly bool knowsSpark;

    public AttributeNamesProducer(
        IEnumerable<PersistentObjectInfo> persistentObjects,
        bool knowsSpark,
        string rootNamespace)
        : base(rootNamespace, "AttributeNames.g.cs")
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
            using (writer.OpenBlock("public static class AttributeNames"))
            {
                foreach (var po in list)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using (writer.OpenBlock($"public static class {po.EntityName}"))
                    {
                        foreach (var attr in po.AttributeNames)
                        {
                            writer.WriteLine($"public const string {attr} = \"{attr}\";");
                        }
                    }
                }
            }
        }
    }
}
