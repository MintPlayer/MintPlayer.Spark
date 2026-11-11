using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class EntityRedirectRegistrationProducer : Producer
{
    private readonly IEnumerable<EntityRedirectClassInfo> redirectClasses;
    private readonly bool knowsRedirects;

    public EntityRedirectRegistrationProducer(
        IEnumerable<EntityRedirectClassInfo> redirectClasses,
        bool knowsRedirects,
        string rootNamespace)
        : base(rootNamespace, "SparkEntityRedirectRegistrations.g.cs")
    {
        this.redirectClasses = redirectClasses;
        this.knowsRedirects = knowsRedirects;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        // A partial class with a base list on two declarations is found twice.
        var redirectTypes = redirectClasses.Select(c => c.RedirectTypeName).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (!knowsRedirects || redirectTypes.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("internal static class SparkEntityRedirectsBuilderExtensions"))
            {
                writer.WriteLine("/// <summary>Registers every ISparkEntityRedirect in this project. Needs AddPrerendering to answer anything.</summary>");
                using (writer.OpenBlock("internal static global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder AddEntityRedirects(this global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder builder)"))
                {
                    foreach (var redirectType in redirectTypes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteLine($"global::{EntityRedirectFilter.RegistrationExtensions}.AddEntityRedirect<{redirectType}>(builder);");
                    }
                    writer.WriteLine("return builder;");
                }
            }
        }
    }
}
