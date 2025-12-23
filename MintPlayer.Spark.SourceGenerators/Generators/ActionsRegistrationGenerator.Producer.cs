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
        writer.WriteLine("using Microsoft.Extensions.DependencyInjection;");
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("internal static class SparkActionsExtensions"))
            {
                using (writer.OpenBlock("internal static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddSparkActions(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)"))
                {
                    foreach (var actionsClass in actionsList)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteLine($"global::MintPlayer.Spark.SparkExtensions.AddSparkActions<{actionsClass.ActionsTypeName}, {actionsClass.EntityTypeName}>(services);");
                    }
                    writer.WriteLine("return services;");
                }
            }
        }
    }
}
