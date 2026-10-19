using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Audit;
using MintPlayer.Spark.Commerce.Models;
using MintPlayer.Spark.Commerce.Pricing;
using MintPlayer.Spark.Commerce.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using static MintPlayer.Spark.Commerce.Tests.Core.CommerceFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Core;

/// <summary>GA3 + default 8 against RavenDB: the price history is append-only, committed with the change and its
/// audit entry, and the Omnibus prior price is read back from it. GA2: publishing numbers versions and freezes them.</summary>
public class PriceHistoryRavenTests : SparkTestDriver
{
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));

    private SparkPriceHistory History()
    {
        var options = Options();
        return new SparkPriceHistory(Store, new FinalPriceCalculator(new FixedOptionsMonitor<SparkCommerceOptions>(options)),
            new CommerceAudit(clock), Microsoft.Extensions.Options.Options.Create(options), clock);
    }

    [Fact]
    public async Task Each_price_change_is_stored_with_its_audit_entry_in_the_same_commit()
    {
        var history = History();
        var item = new TestItem { Price = Eur(20m) };

        using (var session = Store.OpenAsyncSession())
        {
            await history.RecordAsync(session, item, null, "edit");
            await session.SaveChangesAsync();
        }

        using var check = Store.OpenAsyncSession();
        var changes = await check.Advanced.LoadStartingWithAsync<CommercePriceChange>(SparkPriceHistory.Prefix(item.Id!));
        changes.Should().ContainSingle();
        var audits = await check.Advanced.LoadStartingWithAsync<CommerceAuditRecord>($"{CommerceAudit.Collection}/{item.Id}/");
        audits.Should().ContainSingle(a => a.Action == CommerceAuditActions.PriceChanged);
    }

    [Fact]
    public async Task An_announced_reduction_stores_the_lowest_price_of_the_previous_30_days()
    {
        var history = History();
        var item = new TestItem { Price = Eur(20m) };

        await RecordAsync(history, item, null, "edit");
        clock.Now = clock.Now.AddDays(5);
        item.Price = Eur(18m);
        await RecordAsync(history, item, Eur(20m), "edit");
        clock.Now = clock.Now.AddDays(10);
        item.Price = Eur(19m);
        await RecordAsync(history, item, Eur(18m), "edit");
        clock.Now = clock.Now.AddDays(10);
        item.Price = Eur(15m);
        var sale = await RecordAsync(history, item, Eur(19m), "sale-start", isAnnouncedReduction: true);

        sale.PriorPrice.Should().Be(Eur(18m));
        (await history.GetLowestFinalPriceAsync(item.Id!, clock.Now)).Should().Be(Eur(18m));
    }

    [Fact]
    public async Task Publishing_numbers_versions_per_kind_and_owner()
    {
        var legal = new SparkLegalDocuments(Store, clock);
        using (var session = Store.OpenAsyncSession())
        {
            foreach (var id in new[] { "LegalDocuments/a", "LegalDocuments/b" })
                await session.StoreAsync(new LegalDocument
                {
                    Id = id, Kind = LegalDocumentKinds.TermsOfSale,
                    Title = new MintPlayer.Spark.Abstractions.TranslatedString(), Body = new MintPlayer.Spark.Abstractions.TranslatedString(),
                }, id);
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenAsyncSession())
        {
            (await legal.PublishAsync(session, "LegalDocuments/a", "Users/1", default)).Version.Should().Be(1);
            await session.SaveChangesAsync();
        }
        WaitForIndexing(Store);
        using (var session = Store.OpenAsyncSession())
        {
            (await legal.PublishAsync(session, "LegalDocuments/b", "Users/1", default)).Version.Should().Be(2);
            await session.SaveChangesAsync();
        }

        WaitForIndexing(Store);
        var inForce = await legal.GetInForceAsync(null, clock.Now.AddMinutes(1));
        inForce.Single().Version.Should().Be(2);
        (await legal.GetMissingAsync(null, [LegalDocumentKinds.TermsOfSale, LegalDocumentKinds.PrivacyPolicy]))
            .Should().Equal(LegalDocumentKinds.PrivacyPolicy);
    }

    private async Task<PriceHistoryEntry> RecordAsync(SparkPriceHistory history, TestItem item, MintPlayer.Spark.Abstractions.Money? previous, string reason, bool isAnnouncedReduction = false)
    {
        using var session = Store.OpenAsyncSession();
        var entry = await history.RecordAsync(session, item, previous, reason, isAnnouncedReduction);
        await session.SaveChangesAsync();
        return entry;
    }
}
