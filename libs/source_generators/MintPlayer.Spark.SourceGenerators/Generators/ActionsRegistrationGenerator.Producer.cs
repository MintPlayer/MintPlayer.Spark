using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class ActionsRegistrationProducer : Producer
{
    private readonly IEnumerable<ActionsClassInfo> actionsClasses;
    private readonly bool knowsSpark;

    public ActionsRegistrationProducer(
        IEnumerable<ActionsClassInfo> actionsClasses,
        bool knowsSpark,
        string rootNamespace)
        : base(rootNamespace, "SparkActionsRegistrations.g.cs")
    {
        this.actionsClasses = actionsClasses;
        this.knowsSpark = knowsSpark;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var actionsList = actionsClasses.ToList();

        // Don't generate if no actions classes found or project doesn't reference Spark
        if (!knowsSpark || actionsList.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();

        // #660 G17: a class that specialises a same-named actions class (typically a library's) is recorded, so the
        // run-time resolver takes it instead of refusing the two as ambiguous.
        var overrides = actionsList.Where(a => a.OverridesBaseActions).ToList();
        foreach (var actionsClass in overrides)
            writer.WriteLine($"[assembly: global::MintPlayer.Spark.Abstractions.SparkActionsOverride(typeof({actionsClass.EntityTypeName}), typeof({actionsClass.ActionsTypeName}))]");
        if (overrides.Count > 0)
            writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("internal static class SparkActionsBuilderExtensions"))
            {
                using (writer.OpenBlock("internal static global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder AddActions(this global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder builder)"))
                {
                    foreach (var actionsClass in actionsList)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteLine($"global::MintPlayer.Spark.SparkExtensions.AddSparkActions<{actionsClass.ActionsTypeName}, {actionsClass.EntityTypeName}>(builder.Services);");
                    }
                    writer.WriteLine("return builder;");
                }
            }
        }
    }
}
