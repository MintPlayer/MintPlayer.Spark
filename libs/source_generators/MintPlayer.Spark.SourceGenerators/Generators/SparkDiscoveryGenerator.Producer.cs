using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.SourceGenerators.Generators;

public class SparkDiscoveryProducer : Producer
{
    private const string Builder = "global::MintPlayer.Spark.Abstractions.Builder.ISparkBuilder";
    private const string ServiceCollectionExtensions = "global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions";

    private readonly IReadOnlyList<DiscoveredRegistration> registrations;
    private readonly DiscoveryGates gates;

    public SparkDiscoveryProducer(
        IEnumerable<DiscoveredRegistration> registrations,
        DiscoveryGates gates,
        string rootNamespace)
        : base(rootNamespace, "SparkDiscoveryRegistrations.g.cs")
    {
        this.registrations = registrations.ToList();
        this.gates = gates;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        if (!gates.HasBuilder)
            return;

        var actions = Of(DiscoveryKind.Actions, gates.Actions);
        var customActions = Of(DiscoveryKind.CustomAction, gates.CustomActions);
        var recipients = Of(DiscoveryKind.Recipient, gates.Recipients);
        var cronJobs = Of(DiscoveryKind.CronJob, gates.CronJobs);
        var migrations = Of(DiscoveryKind.Migration, gates.Migrations);
        var interceptors = Of(DiscoveryKind.Interceptor, gates.Interceptors)
            .OrderBy(r => r.TypeName, StringComparer.Ordinal)
            .ToList();
        var workers = Of(DiscoveryKind.SubscriptionWorker, gates.SubscriptionWorkers);
        var rowPolicies = Of(DiscoveryKind.RowPolicy, gates.RowPolicies)
            .OrderBy(r => r.TypeName, StringComparer.Ordinal)
            .ToList();
        var contracts = Of(DiscoveryKind.Discoverable, gates.Discoverable)
            .OrderBy(r => r.ServiceTypeName, StringComparer.Ordinal)
            .ThenBy(r => r.TypeName, StringComparer.Ordinal)
            .ToList();

        var any = actions.Count + customActions.Count + recipients.Count + cronJobs.Count + migrations.Count
            + interceptors.Count + workers.Count + rowPolicies.Count + contracts.Count > 0;

        // AddSparkFull calls the method unconditionally, so an AllFeatures app gets it even when empty.
        if (!any && !gates.AllFeatures)
            return;

        writer.WriteLine(Header);
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("internal static class SparkDiscoveryBuilderExtensions"))
            {
                writer.WriteLine("/// <summary>");
                writer.WriteLine("/// Registers everything Spark discovered in this project: Actions classes, custom actions, recipients,");
                writer.WriteLine("/// cron jobs, migrations, interceptors, subscription workers, row policies and [SparkDiscoverable] contracts.");
                writer.WriteLine("/// </summary>");
                using (writer.OpenBlock($"internal static {Builder} AddSparkDiscovered(this {Builder} builder)"))
                {
                    if (actions.Count > 0)
                    {
                        writer.WriteLine("// Actions classes");
                        foreach (var r in actions)
                            writer.WriteLine($"global::MintPlayer.Spark.SparkExtensions.AddSparkActions<{r.TypeName}, {r.ServiceTypeName}>(builder.Services);");
                    }

                    if (customActions.Count > 0)
                    {
                        writer.WriteLine("// Custom actions");
                        foreach (var r in customActions)
                            writer.WriteLine($"{ServiceCollectionExtensions}.AddScoped<{r.TypeName}>(builder.Services);");
                    }

                    if (recipients.Count > 0)
                    {
                        writer.WriteLine("// Message recipients");
                        foreach (var r in recipients)
                        {
                            writer.WriteLine($"{ServiceCollectionExtensions}.AddScoped<global::MintPlayer.Spark.Messaging.Abstractions.IRecipient<{r.ServiceTypeName}>, {r.TypeName}>(builder.Services);");
                            if (r.IsCheckpointRecipient)
                                writer.WriteLine($"{ServiceCollectionExtensions}.AddScoped<global::MintPlayer.Spark.Messaging.Abstractions.ICheckpointRecipient<{r.ServiceTypeName}>, {r.TypeName}>(builder.Services);");
                        }
                    }

                    if (cronJobs.Count > 0)
                    {
                        writer.WriteLine("// Cron jobs");
                        using (writer.OpenBlock("global::MintPlayer.Spark.Cron.SparkCronExtensions.AddCron(builder, cron =>"))
                        {
                            foreach (var r in cronJobs)
                                writer.WriteLine($"cron.AddJob<{r.TypeName}>();");
                        }
                        writer.WriteLine(");");
                    }

                    if (migrations.Count > 0)
                    {
                        writer.WriteLine("// Migrations: this project's, then those referenced packages ship");
                        using (writer.OpenBlock("global::MintPlayer.Spark.Migrations.SparkMigrationsExtensions.AddMigrations(builder, migrations =>"))
                        {
                            foreach (var r in migrations)
                                writer.WriteLine($"migrations.AddMigration<{r.TypeName}>();");
                        }
                        writer.WriteLine(");");
                    }

                    if (interceptors.Count > 0)
                    {
                        writer.WriteLine("// Persistence interceptors");
                        foreach (var r in interceptors)
                            writer.WriteLine($"global::MintPlayer.Spark.Abstractions.Interceptors.SparkBuilderInterceptorExtensions.AddInterceptor<{r.TypeName}>(builder);");
                    }

                    if (workers.Count > 0)
                    {
                        writer.WriteLine("// Subscription workers");
                        foreach (var r in workers)
                            writer.WriteLine($"global::MintPlayer.Spark.SubscriptionWorker.SparkSubscriptionExtensions.AddSubscriptionWorker<{r.TypeName}>(builder.Services);");
                    }

                    if (rowPolicies.Count > 0)
                    {
                        writer.WriteLine("// Row policies");
                        foreach (var r in rowPolicies)
                            writer.WriteLine($"global::MintPlayer.Spark.Extensions.SparkBuilderRowPolicyExtensions.AddSparkRowPolicy<{r.TypeName}>(builder);");
                    }

                    if (contracts.Count > 0)
                    {
                        writer.WriteLine("// [SparkDiscoverable] contracts");
                        foreach (var r in contracts)
                        {
                            var factory = r.Lifetime switch
                            {
                                0 => "Singleton",
                                2 => "Transient",
                                _ => "Scoped",
                            };
                            writer.WriteLine($"global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(builder.Services, global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor.{factory}<{r.ServiceTypeName}, {r.TypeName}>());");
                        }
                    }

                    writer.WriteLine("return builder;");
                }
            }
        }
    }

    /// <summary>
    /// The registrations of one kind, in discovery order, without repeats: a partial class can be
    /// seen once per declaration that carries a base list.
    /// </summary>
    private List<DiscoveredRegistration> Of(DiscoveryKind kind, bool gate)
    {
        if (!gate)
            return new List<DiscoveredRegistration>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return registrations
            .Where(r => r.Kind == kind)
            .Where(r => seen.Add($"{r.TypeName}|{r.ServiceTypeName}"))
            .ToList();
    }
}
