using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #482, D17 — the durable after-commit interceptors delivered by the real Spark Messaging, end to end: the
/// outbox <c>AddMessaging</c> registers, the message written in the save's commit, the type allow-list,
/// the recipient, the framework's dispatcher, and the payload surviving serialization. Every other test
/// of durable interceptors uses <c>TestAfterCommitOutbox</c>, which cannot catch a wiring break here.
/// </summary>
public class DurableAfterCommitMessagingTests : SparkTestDriver
{
    private static readonly Guid NoteTypeId = Guid.Parse("46a1c7e0-4600-4600-4600-46a1c7e04684");

    private readonly InterceptionLog log = new();
    private readonly CommittedLog committed = new();

    [Fact]
    public async Task A_committed_save_reaches_its_durable_interceptor_through_Messaging_with_its_facts()
    {
        await using var factory = new SparkEndpointFactory<DurableContext>(
            Store,
            [InterceptedNoteModel.For(NoteTypeId), DurableAfterCommitInterceptorTests.DurableNoteModel()],
            configureServices: services => services.AddSingleton(log).AddSingleton(committed),
            configureSpark: spark => spark
                .AddMessaging(o => o.FallbackPollInterval = TimeSpan.FromSeconds(1))
                .AddInterceptor<FactRecordingInterceptor>()
                .AddInterceptor<RecordingCommittedInterceptor>());

        string? id;
        using (var scope = factory.CreateScope())
        {
            var po = new PersistentObject { ObjectTypeId = NoteTypeId, Name = "InterceptedNote" };
            po.AddAttribute(new PersistentObjectAttribute { Name = "Title", DataType = "string", Value = "delivered", IsValueChanged = true });
            id = (await scope.ServiceProvider.GetRequiredService<IDatabaseAccess>().SavePersistentObjectAsync(po)).Id;
        }

        await AsyncWait.UntilAsync(() => committed.Changes.Count > 0, "the durable interceptor to run through Messaging", TimeSpan.FromSeconds(30));

        var change = committed.Changes.Should().ContainSingle().Which;
        change.Id.Should().Be(id);
        change.Operation.Should().Be(PersistentObjectOperation.New);
        change.Facts["Title"].Should().Be("delivered", "the facts survived the message payload");

        using var session = Store.OpenAsyncSession();
        var message = await session.Query<SparkMessage>().Customize(c => c.WaitForNonStaleResults()).SingleAsync();
        message.MessageType.Should().Contain(nameof(MintPlayer.Spark.Abstractions.Interceptors.SparkAfterCommitWork));
        await AsyncWait.ForAsync(
            async () =>
            {
                using var read = Store.OpenAsyncSession();
                return await read.LoadAsync<SparkMessage>(message.Id);
            },
            m => m.Status == EMessageStatus.Completed,
            "the after-commit message to complete",
            last => $"Status={last?.Status}",
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_refused_save_publishes_no_message()
    {
        await using var factory = new SparkEndpointFactory<DurableContext>(
            Store,
            [InterceptedNoteModel.For(NoteTypeId), DurableAfterCommitInterceptorTests.DurableNoteModel()],
            configureServices: services => services.AddSingleton(log).AddSingleton(committed),
            configureSpark: spark => spark
                .AddMessaging(o => o.FallbackPollInterval = TimeSpan.FromSeconds(1))
                .AddInterceptor<FactRecordingInterceptor>()
                .AddInterceptor<RecordingCommittedInterceptor>());

        using (var scope = factory.CreateScope())
        {
            var po = new PersistentObject { ObjectTypeId = NoteTypeId, Name = "InterceptedNote" };
            po.AddAttribute(new PersistentObjectAttribute { Name = "Title", DataType = "string", Value = "refuse", IsValueChanged = true });
            var act = () => scope.ServiceProvider.GetRequiredService<IDatabaseAccess>().SavePersistentObjectAsync(po);
            await act.Should().ThrowAsync<SparkValidationException>();
        }

        using var session = Store.OpenAsyncSession();
        (await session.Query<SparkMessage>().Customize(c => c.WaitForNonStaleResults()).CountAsync()).Should().Be(0);
        committed.Changes.Should().BeEmpty();
    }
}
