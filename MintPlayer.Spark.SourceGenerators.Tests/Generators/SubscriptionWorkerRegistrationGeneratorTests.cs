using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using MintPlayer.Spark.SubscriptionWorker;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

public class SubscriptionWorkerRegistrationGeneratorTests
{
    private const string GeneratorName = "SubscriptionWorkerRegistrationGenerator";

    [Fact]
    public void SparkSubscriptionWorker_subclass_is_registered()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Logging;
            using MintPlayer.Spark.SubscriptionWorker;
            using Raven.Client.Documents;
            using Raven.Client.Documents.Subscriptions;

            namespace TestApp.Workers;

            public class CarEventsWorker : SparkSubscriptionWorker<Car>
            {
                public CarEventsWorker(IDocumentStore s, ILogger<CarEventsWorker> l) : base(s, l) { }
                protected override SubscriptionCreationOptions ConfigureSubscription()
                    => new SubscriptionCreationOptions { Query = "from Cars" };
                protected override Task ProcessBatchAsync(SubscriptionBatch<Car> batch, CancellationToken ct)
                    => Task.CompletedTask;
            }

            public class Car
            {
                public string? Id { get; set; }
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(SparkSubscriptionWorker<>)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().ContainSingle();
        var generated = result.GeneratedSources[0].Source;

        generated.Should().Contain("AddSubscriptionWorkers");
        generated.Should().Contain("global::TestApp.Workers.CarEventsWorker");
    }

    [Fact]
    public void No_source_without_SparkSubscriptionWorker_reference()
    {
        var source = """
            namespace TestApp;
            public class Foo { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: Array.Empty<Type>(),
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty();
    }
}
