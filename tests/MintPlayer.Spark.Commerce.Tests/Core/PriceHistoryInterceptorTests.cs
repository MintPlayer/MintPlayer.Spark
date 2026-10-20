using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Commerce.Pricing;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Session;
using static MintPlayer.Spark.Commerce.Tests.Core.CommerceFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Core;

/// <summary>
/// GA3 for every <see cref="ISellableItem"/>: a new item's first price-history entry commits in the save's own session.
/// Red before the fix: the generic interceptor recorded a new item in <c>OnAfterSave</c> with a second
/// <c>SaveChangesAsync</c>, so a failed second commit left an item on sale without its first price entry.
/// </summary>
public class PriceHistoryInterceptorTests : SparkTestDriver
{
    [Fact]
    public async Task A_new_item_gets_its_id_and_first_entry_in_the_save_session()
    {
        var history = new SessionPriceHistory();
        using var session = Store.OpenAsyncSession();
        var item = new TestItem { Id = null, Price = Eur(10m), VatRateBasisPoints = 2100 };

        await Interceptor(history).OnBeforeSaveAsync(item, Context(session, item, before: null, PersistentObjectOperation.New));

        item.Id.Should().NotBeNullOrEmpty();
        history.Calls.Should().HaveCount(1);
        history.Calls[0].Session.Should().BeSameAs(session);
        history.Calls[0].Reason.Should().Be("create");
        using (var other = Store.OpenAsyncSession())
            (await other.Advanced.LoadStartingWithAsync<PriceMark>("PriceMarks/")).Should().BeEmpty();

        // The pipeline stores the entity again (a no-op for a tracked one) and commits once: item and entry together.
        await session.StoreAsync(item);
        await session.SaveChangesAsync();
        using var check = Store.OpenAsyncSession();
        (await check.LoadAsync<TestItem>(item.Id)).Should().NotBeNull();
        (await check.Advanced.LoadStartingWithAsync<PriceMark>("PriceMarks/")).Should().HaveCount(1);
    }

    [Fact]
    public async Task An_edited_price_is_recorded_in_the_save_session_and_an_unchanged_one_is_not()
    {
        var history = new SessionPriceHistory();
        using var session = Store.OpenAsyncSession();
        var before = new TestItem { Price = Eur(10m), VatRateBasisPoints = 2100 };

        await Interceptor(history).OnBeforeSaveAsync(new TestItem { Price = Eur(10m), VatRateBasisPoints = 2100 },
            Context(session, before, before, PersistentObjectOperation.Save));
        history.Calls.Should().BeEmpty();

        var edited = new TestItem { Price = Eur(8m), VatRateBasisPoints = 2100 };
        await Interceptor(history).OnBeforeSaveAsync(edited, Context(session, edited, before, PersistentObjectOperation.Save));
        history.Calls.Should().HaveCount(1);
        history.Calls[0].Session.Should().BeSameAs(session);
        history.Calls[0].Reason.Should().Be("edit");
    }

    private static PriceHistoryInterceptors Interceptor(ISparkPriceHistory history)
        => new(history, new FinalPriceCalculator(new FixedOptionsMonitor<SparkCommerceOptions>(Options())));

    private static SaveContext Context(IAsyncDocumentSession session, TestItem item, TestItem? before, PersistentObjectOperation operation) => new()
    {
        EntityType = typeof(TestItem),
        Operation = operation,
        PersistentObject = new PersistentObject { Id = item.Id, Name = nameof(TestItem), ObjectTypeId = Guid.NewGuid() },
        Entity = item,
        Before = before,
        Session = session,
    };

    private sealed class PriceMark
    {
        public string? Id { get; set; }
        public string ItemId { get; set; } = "";
    }

    /// <summary>Records each call and writes a marker document into the caller's session, as the real history does.</summary>
    private sealed class SessionPriceHistory : ISparkPriceHistory
    {
        public List<(IAsyncDocumentSession Session, string ItemId, string Reason)> Calls { get; } = [];

        public async Task<PriceHistoryEntry> RecordAsync(IAsyncDocumentSession session, ISellableItem item, Money? previous, string reason,
            bool isAnnouncedReduction = false, CancellationToken cancellationToken = default)
        {
            Calls.Add((session, item.Id!, reason));
            await session.StoreAsync(new PriceMark { ItemId = item.Id! }, $"PriceMarks/{Calls.Count}", cancellationToken);
            return new PriceHistoryEntry(item.Id!, DateTimeOffset.UnixEpoch, previous, item.Price!, item.Price!, reason, null, null);
        }

        public Task<Money?> GetLowestFinalPriceAsync(string itemId, DateTimeOffset before, TimeSpan? window = null, CancellationToken cancellationToken = default)
            => Task.FromResult<Money?>(null);
    }
}
