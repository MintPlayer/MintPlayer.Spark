using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK060 (#551 D13, <c>docs/spark_editor_PRD.md</c>): a feature configured in the same body as a store provider
/// that cannot serve it. The fixture declares its own provider and feature methods with the real attributes, as
/// <c>UseInMemory</c>/<c>AddMessaging</c> do in their packages.
/// </summary>
public class StoreRequirementAnalyzerTests
{
    private const string Registrations = """
        using MintPlayer.Spark.Abstractions.Storage;

        namespace TestApp;

        public sealed class Builder { }

        public static class Registrations
        {
            [SparkStoreProvider(SparkStoreProviders.RavenDb, SparkStoreCapabilities.Subscriptions | SparkStoreCapabilities.Revisions)]
            public static Builder UseRaven(this Builder b) => b;

            [SparkStoreProvider(SparkStoreProviders.InMemory, SparkStoreCapabilities.ChangeNotifications)]
            public static Builder UseMemory(this Builder b) => b;

            [SparkRequiresStore("Spark.Messaging", SparkStoreCapabilities.Subscriptions)]
            public static Builder AddQueue(this Builder b) => b;

            [SparkRequiresStore("Spark.Cron", Provider = SparkStoreProviders.RavenDb)]
            public static Builder AddSchedule(this Builder b) => b;

            public static void Configure(System.Action<Builder> configure) => configure(new Builder());
        }
        """;

    private static async Task<IReadOnlyList<Diagnostic>> RunAsync(string body)
    {
        var program = $$"""
            namespace TestApp;

            public static class Program
            {
                public static void Main()
                {
                    {{body}}
                }
            }
            """;
        return await GeneratorHarness.RunAnalyzerAsync(
            "StoreRequirementAnalyzer", [Registrations, program], referenceTypes: [typeof(SparkStoreCapabilities)]);
    }

    [Fact]
    public async Task A_missing_capability_is_reported_at_the_feature_call()
    {
        var diagnostics = await RunAsync("Registrations.Configure(spark => { spark.UseMemory(); spark.AddQueue(); });");

        diagnostics.Should().ContainSingle();
        diagnostics[0].Id.Should().Be("SPARK060");
        diagnostics[0].GetMessage().Should().Be(
            "Spark.Messaging needs Subscriptions; the InMemory provider doesn't offer it. The host refuses this configuration at startup.");
    }

    [Fact]
    public async Task A_feature_bound_to_another_provider_is_reported()
    {
        var diagnostics = await RunAsync("Registrations.Configure(spark => { spark.UseMemory(); spark.AddSchedule(); });");

        diagnostics.Should().ContainSingle()
            .Which.GetMessage().Should().StartWith("Spark.Cron runs on RavenDB only; this configuration uses the InMemory provider");
    }

    [Fact]
    public async Task A_capable_provider_is_clean()
    {
        var diagnostics = await RunAsync("Registrations.Configure(spark => { spark.UseRaven(); spark.AddQueue(); spark.AddSchedule(); });");

        diagnostics.Should().BeEmpty();
    }

    /// <summary>The store may be registered elsewhere; that case is the startup check's.</summary>
    [Fact]
    public async Task A_body_without_a_provider_is_clean()
    {
        var diagnostics = await RunAsync("Registrations.Configure(spark => spark.AddQueue());");

        diagnostics.Should().BeEmpty();
    }

    /// <summary>Each lambda is its own body: a provider in one does not judge the features of another.</summary>
    [Fact]
    public async Task A_provider_in_another_lambda_is_not_compared()
    {
        var diagnostics = await RunAsync("""
            Registrations.Configure(spark => spark.UseMemory());
            Registrations.Configure(spark => spark.AddQueue());
            """);

        diagnostics.Should().BeEmpty();
    }
}
