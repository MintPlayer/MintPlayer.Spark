using MintPlayer.Spark.AllFeatures.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;
using System.CodeDom.Compiler;

namespace MintPlayer.Spark.AllFeatures.SourceGenerators.Generators;

public class SparkFullProducer : Producer
{
    private readonly IEnumerable<SparkFullDiscoveredType> discoveries;
    private readonly SparkFullFeatureFlags flags;

    public SparkFullProducer(
        IEnumerable<SparkFullDiscoveredType> discoveries,
        SparkFullFeatureFlags flags,
        string rootNamespace)
        : base(rootNamespace, "SparkFullRegistrations.g.cs")
    {
        this.discoveries = discoveries;
        this.flags = flags;
    }

    protected override void ProduceSource(IndentedTextWriter writer, CancellationToken cancellationToken)
    {
        if (!flags.HasSpark)
            return;

        var discoveryList = discoveries.ToList();

        // Find the SparkContext subclass (required)
        var contextType = discoveryList.FirstOrDefault(d => d.Kind == "Context")?.TypeName;
        if (contextType == null)
            return;

        // Find the SparkUser subclass, or fall back to SparkUser if Authorization is referenced
        var userType = discoveryList.FirstOrDefault(d => d.Kind == "User")?.TypeName;
        if (userType == null && flags.HasSparkUser)
            userType = "global::MintPlayer.Spark.Authorization.Identity.SparkUser";

        var hasActions = discoveryList.Any(d => d.Kind == "Actions");
        var hasCustomActions = discoveryList.Any(d => d.Kind == "CustomAction");
        var hasRecipients = discoveryList.Any(d => d.Kind == "Recipient");

        writer.WriteLine(Header);
        writer.WriteLine();
        writer.WriteLine("#nullable enable");
        writer.WriteLine();

        using (writer.OpenBlock($"namespace {RootNamespace}"))
        {
            using (writer.OpenBlock("internal static class SparkFullBuilderExtensions"))
            {
                WriteAddSparkFull(writer, contextType, userType, hasActions, hasCustomActions, hasRecipients);
                writer.WriteLine();
                WriteUseSparkFull(writer, contextType);
                writer.WriteLine();
                WriteMapSparkFull(writer);
            }
        }
    }

    private void WriteAddSparkFull(
        IndentedTextWriter writer,
        string contextType,
        string? userType,
        bool hasActions,
        bool hasCustomActions,
        bool hasRecipients)
    {
        writer.WriteLine("/// <summary>");
        writer.WriteLine("/// Registers all Spark services, modules, and discovered actions/recipients.");
        writer.WriteLine("/// </summary>");
        using (writer.OpenBlock("public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddSparkFull(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services, global::Microsoft.Extensions.Configuration.IConfiguration configuration, global::System.Action<global::MintPlayer.Spark.AllFeatures.SparkFullOptions>? configure = null)"))
        {
            writer.WriteLine("var options = new global::MintPlayer.Spark.AllFeatures.SparkFullOptions();");
            writer.WriteLine("configure?.Invoke(options);");
            writer.WriteLine();

            using (writer.OpenBlock("global::MintPlayer.Spark.SparkExtensions.AddSpark(services, configuration, spark =>"))
            {
                writer.WriteLine($"global::MintPlayer.Spark.SparkExtensions.UseContext<{contextType}>(spark);");

                if (hasActions)
                    writer.WriteLine("spark.AddActions();");
                if (hasCustomActions)
                    writer.WriteLine("spark.AddCustomActions();");
                if (hasRecipients)
                    writer.WriteLine("spark.AddRecipients();");

                if (flags.HasAuthorization)
                    writer.WriteLine("global::MintPlayer.Spark.Authorization.Extensions.SparkBuilderAuthorizationExtensions.AddAuthorization(spark, options.Authorization);");

                if (userType != null)
                    writer.WriteLine($"global::MintPlayer.Spark.Authorization.Extensions.SparkBuilderAuthorizationExtensions.AddAuthentication<{userType}>(spark, options.Identity, options.IdentityProviders);");

                if (flags.HasMessaging)
                    writer.WriteLine("global::MintPlayer.Spark.Messaging.SparkBuilderMessagingExtensions.AddMessaging(spark, options.Messaging);");

                if (flags.HasReplication)
                {
                    using (writer.OpenBlock("if (options.Replication != null)"))
                    {
                        writer.WriteLine("global::MintPlayer.Spark.Replication.SparkBuilderReplicationExtensions.AddReplication(spark, options.Replication);");
                    }
                }
            }
            writer.WriteLine(");");
            writer.WriteLine("return services;");
        }
    }

    private void WriteUseSparkFull(IndentedTextWriter writer, string contextType)
    {
        writer.WriteLine("/// <summary>");
        writer.WriteLine("/// Configures Spark middleware and synchronizes models if --spark-synchronize-model is passed.");
        writer.WriteLine("/// </summary>");
        using (writer.OpenBlock("public static global::Microsoft.AspNetCore.Builder.IApplicationBuilder UseSparkFull(this global::Microsoft.AspNetCore.Builder.IApplicationBuilder app, string[] args)"))
        {
            using (writer.OpenBlock("global::MintPlayer.Spark.SparkExtensions.UseSpark(app, spark =>"))
            {
                writer.WriteLine($"spark.SynchronizeModelsIfRequested<{contextType}>(args);");
            }
            writer.WriteLine(");");
            writer.WriteLine("return app;");
        }
    }

    private static void WriteMapSparkFull(IndentedTextWriter writer)
    {
        writer.WriteLine("/// <summary>");
        writer.WriteLine("/// Maps all Spark endpoints.");
        writer.WriteLine("/// </summary>");
        using (writer.OpenBlock("public static global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder MapSparkFull(this global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)"))
        {
            writer.WriteLine("global::MintPlayer.Spark.SparkExtensions.MapSpark(endpoints);");
            writer.WriteLine("return endpoints;");
        }
    }
}
