using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

public class RecipientRegistrationGeneratorTests
{
    private const string GeneratorName = "RecipientRegistrationGenerator";

    [Fact]
    public void IRecipient_implementation_is_registered_with_the_message_type()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Messaging.Abstractions;

            namespace TestApp.Messaging;

            public record OrderPlaced(string Id, decimal Amount);

            public class OrderHandler : IRecipient<OrderPlaced>
            {
                public Task HandleAsync(OrderPlaced m, CancellationToken ct = default)
                    => Task.CompletedTask;
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(IRecipient<>)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().ContainSingle();
        var generated = result.GeneratedSources[0].Source;

        generated.Should().Contain("internal static class SparkRecipientsBuilderExtensions");
        generated.Should().Contain("AddRecipients");
        generated.Should().Contain("IRecipient<global::TestApp.Messaging.OrderPlaced>, global::TestApp.Messaging.OrderHandler");
        // The checkpoint-specific registration must NOT appear for a plain IRecipient handler.
        generated.Should().NotContain("ICheckpointRecipient");
    }

    [Fact]
    public void ICheckpointRecipient_implementation_registers_both_IRecipient_and_ICheckpointRecipient()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Messaging.Abstractions;

            namespace TestApp.Messaging;

            public record BigJob(string Id);

            public class BigJobHandler : ICheckpointRecipient<BigJob>
            {
                public Task HandleAsync(BigJob m, CancellationToken ct = default)
                    => Task.CompletedTask;
                public Task HandleAsync(BigJob m, string checkpoint, CancellationToken ct = default)
                    => Task.CompletedTask;
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(IRecipient<>), typeof(ICheckpointRecipient<>)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().ContainSingle();
        var generated = result.GeneratedSources[0].Source;

        generated.Should().Contain("IRecipient<global::TestApp.Messaging.BigJob>, global::TestApp.Messaging.BigJobHandler");
        generated.Should().Contain("ICheckpointRecipient<global::TestApp.Messaging.BigJob>, global::TestApp.Messaging.BigJobHandler");
    }

    [Fact]
    public void No_source_when_messaging_abstractions_not_referenced()
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
