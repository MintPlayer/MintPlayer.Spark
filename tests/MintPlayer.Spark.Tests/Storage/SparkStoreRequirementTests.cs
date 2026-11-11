using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage.InMemory;
using NSubstitute;

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

    // ---- D20: index bindings in the model -------------------------------------------------------------------

    private static IModelLoader Model(EntityTypeDefinition[] types, SparkQuery[] queries)
    {
        var loader = Substitute.For<IModelLoader>();
        loader.GetEntityTypes().Returns(types);
        loader.GetQueries().Returns(queries);
        return loader;
    }

    [Fact]
    public void An_index_binding_on_a_store_without_indexes_names_every_binding()
    {
        var model = Model(
            [new EntityTypeDefinition { Id = Guid.NewGuid(), Name = "Person", IndexName = "People_Overview" }],
            [
                new SparkQuery { Id = Guid.NewGuid(), Name = "GetPeople", Source = "Database.People", IndexName = "People_Overview" },
                new SparkQuery { Id = Guid.NewGuid(), Name = "GetCars", Source = "Database.Cars" },
            ]);

        var unmet = SparkIndexBindingStartupCheck.Unmet(model, InMemory);

        unmet.Should().ContainSingle();
        unmet[0].Should().Contain("The InMemory provider has no indexes");
        unmet[0].Should().Contain("entity type 'Person' (indexName 'People_Overview')");
        unmet[0].Should().Contain("query 'GetPeople' (indexName 'People_Overview')");
        unmet[0].Should().NotContain("GetCars");
    }

    [Fact]
    public void A_model_without_index_bindings_or_a_store_with_indexes_passes()
    {
        var bound = Model([], [new SparkQuery { Id = Guid.NewGuid(), Name = "GetPeople", Source = "Database.People", IndexName = "People_Overview" }]);
        var withIndexes = Substitute.For<ISparkStore>();
        withIndexes.Capabilities.Returns(SparkStoreCapabilities.Indexes);

        SparkIndexBindingStartupCheck.Unmet(Model([], [new SparkQuery { Id = Guid.NewGuid(), Name = "GetPeople", Source = "Database.People" }]), InMemory)
            .Should().BeEmpty();
        SparkIndexBindingStartupCheck.Unmet(bound, withIndexes).Should().BeEmpty();
        SparkIndexBindingStartupCheck.Unmet(bound, null).Should().BeEmpty();
    }
}
