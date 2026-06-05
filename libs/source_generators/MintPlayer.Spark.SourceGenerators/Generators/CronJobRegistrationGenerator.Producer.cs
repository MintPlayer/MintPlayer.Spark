using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class CronJobRegistrationProducer : Producer
{
    private readonly IEnumerable<CronJobClassInfo> jobClasses;
    private readonly bool knowsCron;

    public CronJobRegistrationProducer(
        IEnumerable<CronJobClassInfo> jobClasses,
        bool knowsCron,
        string rootNamespace)
        : base(rootNamespace, "SparkCronJobRegistrations.g.cs")
    {
        this.jobClasses = jobClasses;
        this.knowsCron = knowsCron;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        var jobList = jobClasses.ToList();

        // Don't generate if no cron jobs found or the project doesn't reference MintPlayer.Spark.Cron
        if (!knowsCron || jobList.Count == 0)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("internal static class SparkCronJobsBuilderExtensions"))
            {
                using (writer.OpenBlock("internal static global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder AddCronJobs(this global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder builder)"))
                {
                    using (writer.OpenBlock("global::MintPlayer.Spark.Cron.SparkCronExtensions.AddCron(builder, cron =>"))
                    {
                        foreach (var jobClass in jobList)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            writer.WriteLine($"cron.AddJob<{jobClass.JobTypeName}>();");
                        }
                    }
                    writer.WriteLine(");");
                    writer.WriteLine("return builder;");
                }
            }
        }
    }
}
