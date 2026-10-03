using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class HookRegistrationProducer : Producer
{
    private readonly IEnumerable<HookClassInfo> hookClasses;
    private readonly bool knowsHooks;

    public HookRegistrationProducer(
        IEnumerable<HookClassInfo> hookClasses,
        bool knowsHooks,
        string rootNamespace)
        : base(rootNamespace, "SparkHookRegistrations.g.cs")
    {
        this.hookClasses = hookClasses;
        this.knowsHooks = knowsHooks;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var hookList = hookClasses
            .GroupBy(h => h.HookTypeName)
            .Select(g => g.First())
            .OrderBy(h => h.HookTypeName, StringComparer.Ordinal)
            .ToList();

        if (!knowsHooks || hookList.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("internal static class SparkHooksBuilderExtensions"))
            {
                using (writer.OpenBlock("internal static global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder AddHooks(this global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder builder)"))
                {
                    foreach (var hookClass in hookList)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteLine($"global::MintPlayer.Spark.Abstractions.Interceptors.SparkBuilderHookExtensions.AddHook<{hookClass.HookTypeName}>(builder);");
                    }
                    writer.WriteLine("return builder;");
                }
            }
        }
    }
}
