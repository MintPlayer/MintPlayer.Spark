using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// R2-H6 — IMessageTypeAllowList builds its set from registered IRecipient&lt;T&gt;
/// services at startup. Types not on the list are dead-lettered before
/// Type.GetType + JsonConvert.DeserializeObject runs, closing the polymorphic
/// deserialization gadget surface that arbitrary MessageType strings (writable
/// pre-mTLS via /spark/sync/apply) would otherwise open.
/// </summary>
public class MessageTypeAllowListTests : SparkTestDriver
{
    public sealed class FooMessage { }
    public sealed class BarMessage { }
    public sealed class UnregisteredMessage { }

    public sealed class FooRecipient : IRecipient<FooMessage>
    {
        public Task HandleAsync(FooMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void Allow_list_contains_registered_message_types()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSparkMessaging();
        services.AddScoped<IRecipient<FooMessage>, FooRecipient>();

        using var provider = services.BuildServiceProvider();
        var allowList = provider.GetRequiredService<IMessageTypeAllowList>();

        allowList.IsAllowedMessageType(typeof(FooMessage).AssemblyQualifiedName)
            .Should().BeTrue("FooMessage has a registered IRecipient<>");
        allowList.IsAllowedHandlerType(typeof(FooRecipient).AssemblyQualifiedName)
            .Should().BeTrue("FooRecipient is the registered implementation");
    }

    [Fact]
    public void Allow_list_refuses_unregistered_message_types()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSparkMessaging();
        services.AddScoped<IRecipient<FooMessage>, FooRecipient>();

        using var provider = services.BuildServiceProvider();
        var allowList = provider.GetRequiredService<IMessageTypeAllowList>();

        allowList.IsAllowedMessageType(typeof(UnregisteredMessage).AssemblyQualifiedName)
            .Should().BeFalse("UnregisteredMessage has no registered IRecipient<>");
        allowList.IsAllowedMessageType(typeof(BarMessage).AssemblyQualifiedName)
            .Should().BeFalse("BarMessage has no registered IRecipient<>");
    }

    [Fact]
    public void Allow_list_refuses_gadget_types_like_FileSystemInfo()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSparkMessaging();
        services.AddScoped<IRecipient<FooMessage>, FooRecipient>();

        using var provider = services.BuildServiceProvider();
        var allowList = provider.GetRequiredService<IMessageTypeAllowList>();

        // FileSystemInfo is a classic Newtonsoft deserialization-gadget target.
        // The allow-list MUST reject it even though Type.GetType could resolve
        // the name.
        allowList.IsAllowedMessageType(typeof(System.IO.FileSystemInfo).AssemblyQualifiedName)
            .Should().BeFalse("framework gadget types must never be on the allow-list");
        allowList.IsAllowedMessageType("System.IO.FileSystemInfo, System.IO.FileSystem")
            .Should().BeFalse("alternative AQN encoding of a gadget type still refused");
    }

    [Fact]
    public void Allow_list_handles_null_or_empty_input_safely()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSparkMessaging();

        using var provider = services.BuildServiceProvider();
        var allowList = provider.GetRequiredService<IMessageTypeAllowList>();

        allowList.IsAllowedMessageType(null).Should().BeFalse();
        allowList.IsAllowedMessageType("").Should().BeFalse();
        allowList.IsAllowedHandlerType(null).Should().BeFalse();
        allowList.IsAllowedHandlerType("").Should().BeFalse();
    }
}
