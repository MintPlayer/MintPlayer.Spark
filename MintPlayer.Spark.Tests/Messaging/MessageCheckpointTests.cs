using System.Reflection;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Messaging;

public class MessageCheckpointTests
{
    [Fact]
    public async Task SaveAsync_throws_before_SetContext_is_called()
    {
        var checkpoint = new MessageCheckpoint();

        var act = async () => await checkpoint.SaveAsync("step-1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*only be used inside a message handler*");
    }

    [Fact]
    public async Task SaveAsync_writes_the_checkpoint_onto_the_HandlerExecution_and_calls_SaveChangesAsync()
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        var handler = new HandlerExecution { HandlerType = "X.Y.Handler" };
        var checkpoint = new MessageCheckpoint();

        // SetContext is internal — invoke via the same-assembly-visible API.
        typeof(MessageCheckpoint)
            .GetMethod("SetContext", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(checkpoint, [session, handler]);

        await checkpoint.SaveAsync("step-a-done");

        handler.Checkpoint.Should().Be("step-a-done");
        await session.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consecutive_SaveAsync_calls_overwrite_the_previous_checkpoint_value()
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        var handler = new HandlerExecution { HandlerType = "X.Y.Handler" };
        var checkpoint = new MessageCheckpoint();

        typeof(MessageCheckpoint)
            .GetMethod("SetContext", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(checkpoint, [session, handler]);

        await checkpoint.SaveAsync("step-a-done");
        await checkpoint.SaveAsync("step-b-done");

        handler.Checkpoint.Should().Be("step-b-done");
        await session.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
