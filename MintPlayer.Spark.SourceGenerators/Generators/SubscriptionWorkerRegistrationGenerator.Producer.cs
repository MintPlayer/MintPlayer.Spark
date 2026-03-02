using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class SubscriptionWorkerRegistrationProducer : Producer
{
    private readonly IEnumerable<SubscriptionWorkerClassInfo> workerClasses;
    private readonly bool knowsSubscriptionWorker;

    public SubscriptionWorkerRegistrationProducer(
        IEnumerable<SubscriptionWorkerClassInfo> workerClasses,
        bool knowsSubscriptionWorker,
        string rootNamespace)
        : base(rootNamespace, "SparkSubscriptionWorkerRegistrations.g.cs")
    {
        this.workerClasses = workerClasses;
        this.knowsSubscriptionWorker = knowsSubscriptionWorker;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var workerList = workerClasses.ToList();

        // Don't generate if no worker classes found or project doesn't reference Spark.SubscriptionWorker
        if (!knowsSubscriptionWorker || workerList.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("using Microsoft.Extensions.DependencyInjection;");
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("internal static class SparkSubscriptionWorkersExtensions"))
            {
                using (writer.OpenBlock("internal static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddSparkSubscriptionWorkers(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)"))
                {
                    foreach (var workerClass in workerList)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteLine($"global::MintPlayer.Spark.SubscriptionWorker.SparkSubscriptionExtensions.AddSubscriptionWorker<{workerClass.WorkerTypeName}>(services);");
                    }
                    writer.WriteLine("return services;");
                }
            }
        }
    }
}
