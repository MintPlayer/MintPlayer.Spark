using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Storage.InMemory;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>The startup half of #551 D13: what <c>UseSpark()</c> says about requirements the store does not meet.</summary>
public class SparkStoreRequirementTests
{
    private static readonly ISparkStore InMemory = new InMemorySparkStore(new InMemoryDatabase());

    [Fact]
    public void A_missing_capability_names_the_feature_and_the_capability()
    {
        var unmet = SparkStoreRequirementExtensions.Unmet(
            [new SparkStoreRequirement("Spark.History", SparkStoreCapabilities.Revisions)], InMemory);

        unmet.Should().Equal("Spark.History needs Revisions; the InMemory provider doesn't offer it.");
    }

    [Fact]
    public void A_feature_bound_to_another_provider_is_refused_even_without_capabilities()
    {
        var unmet = SparkStoreRequirementExtensions.Unmet(
            [new SparkStoreRequirement("Spark.Cron", SparkStoreCapabilities.None, SparkStoreProviders.RavenDb)], InMemory);

        unmet.Should().Equal("Spark.Cron runs on RavenDB only; this application uses the InMemory provider.");
    }

    [Fact]
    public void An_offered_capability_and_a_duplicate_declaration_are_met_or_reported_once()
    {
        var requirement = new SparkStoreRequirement("Spark.Messaging", SparkStoreCapabilities.Subscriptions);

        SparkStoreRequirementExtensions.Unmet(
            [new SparkStoreRequirement("Spark.Live", SparkStoreCapabilities.ChangeNotifications)], InMemory).Should().BeEmpty();
        SparkStoreRequirementExtensions.Unmet([requirement, requirement], InMemory).Should().HaveCount(1);
    }

    [Fact]
    public void No_store_says_which_provider_to_register()
    {
        var unmet = SparkStoreRequirementExtensions.Unmet([new SparkStoreRequirement("Spark.Messaging", SparkStoreCapabilities.Subscriptions)], null);

        unmet.Should().Equal("Spark.Messaging needs a store, and none is registered: call spark.UseRavenDb() in AddSpark.");
    }
}
