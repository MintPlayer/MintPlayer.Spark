using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class AttributeDescriptionsProducer : Producer
{
    private readonly IEnumerable<AttributeDescriptionInfo> descriptions;
    private readonly bool knowsSpark;

    public AttributeDescriptionsProducer(
        IEnumerable<AttributeDescriptionInfo> descriptions,
        bool knowsSpark,
        string rootNamespace)
        : base(rootNamespace, "SparkAttributeDescriptions.g.cs")
    {
        this.descriptions = descriptions;
        this.knowsSpark = knowsSpark;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var list = descriptions
            .OrderBy(d => d.TypeSortKey, StringComparer.Ordinal)
            .ThenBy(d => d.PropertyName, StringComparer.Ordinal)
            .ToList();

        if (!knowsSpark || list.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("// One line per documented public read/write property. Applications of this attribute are");
        writer.WriteLine("// [Conditional(\"DEBUG\")]: a Release build of this assembly contains none of them.");
        writer.WriteLine();

        foreach (var d in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteLine(
                $"[assembly: global::MintPlayer.Spark.Abstractions.SparkAttributeDescription(" +
                $"typeof({d.TypeOfExpression}), " +
                $"{SymbolDisplay.FormatLiteral(d.PropertyName, quote: true)}, " +
                $"{SymbolDisplay.FormatLiteral(d.Summary, quote: true)})]");
        }
    }
}
