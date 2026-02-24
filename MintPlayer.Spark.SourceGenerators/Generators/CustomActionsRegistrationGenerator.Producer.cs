using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class CustomActionsRegistrationProducer : Producer
{
    private readonly IEnumerable<CustomActionClassInfo> actionClasses;
    private readonly bool knowsCustomAction;

    public CustomActionsRegistrationProducer(
        IEnumerable<CustomActionClassInfo> actionClasses,
        bool knowsCustomAction,
        string rootNamespace)
        : base(rootNamespace, "SparkCustomActionsRegistrations.g.cs")
    {
        this.actionClasses = actionClasses;
        this.knowsCustomAction = knowsCustomAction;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var actionsList = actionClasses.ToList();

        // Don't generate if no custom action classes found or project doesn't reference Spark.Abstractions
        if (!knowsCustomAction || actionsList.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("using Microsoft.Extensions.DependencyInjection;");
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("internal static class SparkCustomActionsExtensions"))
            {
                using (writer.OpenBlock("internal static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddSparkCustomActions(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)"))
                {
                    foreach (var actionClass in actionsList)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteLine($"services.AddScoped<{actionClass.TypeName}>();");
                    }
                    writer.WriteLine("return services;");
                }
            }
        }
    }
}
