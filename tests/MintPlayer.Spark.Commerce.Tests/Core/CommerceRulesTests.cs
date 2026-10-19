using MintPlayer.Spark.Commerce.Jobs;
using MintPlayer.Spark.Commerce.Models;
using MintPlayer.Spark.Commerce.Pricing;
using MintPlayer.Spark.Commerce.Services;
using static MintPlayer.Spark.Commerce.Tests.Core.CommerceFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Core;

/// <summary>The pure rules behind GA2, GA3, GA5, GA6 and GB14.</summary>
public class CommerceRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static PriceHistoryEntry Entry(int day, decimal price)
        => new("Products/1-A", T0.AddDays(day), null, Eur(price), Eur(price), "edit", null, null);

    [Fact]
    public void The_prior_price_is_the_lowest_final_price_in_force_during_the_30_days_before()
    {
        // 20 from day 0, 15 from day 10, 25 from day 35; a reduction on day 50 looks back to day 20.
        var history = new[] { Entry(0, 20m), Entry(10, 15m), Entry(35, 25m) };

        // Day 20–35 the price was 15 (set on day 10, still in force inside the window).
        SparkPriceHistory.Lowest(history, T0.AddDays(20), T0.AddDays(50)).Should().Be(Eur(15m));
        // Window 40–50: only 25 applied.
        SparkPriceHistory.Lowest(history, T0.AddDays(40), T0.AddDays(50)).Should().Be(Eur(25m));
        // Before any price: none.
        (SparkPriceHistory.Lowest(history, T0.AddDays(-40), T0.AddDays(-1)) is null).Should().BeTrue();
    }

    [Fact]
    public void Changes_after_the_reduction_do_not_count()
    {
        var history = new[] { Entry(0, 20m), Entry(40, 5m) };
        SparkPriceHistory.Lowest(history, T0.AddDays(5), T0.AddDays(35)).Should().Be(Eur(20m));
    }

    private static LegalDocument Doc(string kind, string? org, int version, int effectiveDay, bool published = true)
        => new() { Id = $"LegalDocuments/{kind}-{org}-{version}", Kind = kind, OrganizationId = org, Version = version, EffectiveFrom = T0.AddDays(effectiveDay), PublishedAt = published ? T0 : null };

    [Fact]
    public void The_sellers_own_document_wins_over_the_platforms_and_the_latest_version_in_force_counts()
    {
        var docs = new[]
        {
            Doc(LegalDocumentKinds.TermsOfSale, null, 1, 0),
            Doc(LegalDocumentKinds.TermsOfSale, "Organizations/a", 1, 0),
            Doc(LegalDocumentKinds.TermsOfSale, "Organizations/a", 2, 30), // not yet in force on day 10
            Doc(LegalDocumentKinds.PrivacyPolicy, null, 3, 0),
            Doc(LegalDocumentKinds.PrivacyPolicy, null, 4, 0, published: false),
        };

        var inForce = SparkLegalDocuments.InForce(docs, "Organizations/a", T0.AddDays(10)).ToList();

        var terms = inForce.Single(d => d.Kind == LegalDocumentKinds.TermsOfSale);
        terms.OrganizationId.Should().Be("Organizations/a");
        terms.Version.Should().Be(1);
        inForce.Single(d => d.Kind == LegalDocumentKinds.PrivacyPolicy).Version.Should().Be(3);
    }

    [Fact]
    public void A_published_legal_document_is_never_edited()
    {
        var act = () => SparkLegalDocuments.EnsureEditable(Doc(LegalDocumentKinds.TermsOfSale, null, 1, 0));
        act.Should().Throw<MintPlayer.Spark.Abstractions.SparkValidationException>();
    }

    [Fact]
    public void Dac7_reminds_twice_then_blocks_payouts_after_60_days()
    {
        var options = new CommerceDac7Options();
        var data = new Dac7Data();

        Dac7ReminderJob.Next(data, options, T0).Should().Be(Dac7ReminderJob.Dac7Step.Remind); // first request
        data.RequestedAt = T0; data.RemindersSent = 1; data.LastReminderAt = T0;
        Dac7ReminderJob.Next(data, options, T0.AddDays(1)).Should().Be(Dac7ReminderJob.Dac7Step.None);
        Dac7ReminderJob.Next(data, options, T0.AddDays(14)).Should().Be(Dac7ReminderJob.Dac7Step.Remind);
        data.RemindersSent = 2; data.LastReminderAt = T0.AddDays(14);
        Dac7ReminderJob.Next(data, options, T0.AddDays(30)).Should().Be(Dac7ReminderJob.Dac7Step.None); // < 60 days
        Dac7ReminderJob.Next(data, options, T0.AddDays(60)).Should().Be(Dac7ReminderJob.Dac7Step.Block);
    }

    [Fact]
    public void Complete_dac7_data_needs_nothing()
    {
        var data = new Dac7Data { Tin = "1", TinCountry = "BE", PrimaryAddress = "x", Iban = "BE00", AccountHolder = "x", BirthDate = new DateOnly(1990, 1, 1) };
        Dac7ReminderJob.Next(data, new CommerceDac7Options(), T0).Should().Be(Dac7ReminderJob.Dac7Step.None);
    }

    [Fact]
    public void A_seller_sells_only_when_approved_declared_verified_and_kyc_verified_in_connected_mode()
    {
        var options = Options(o => o.Merchant = MerchantMode.ConnectedSellers);
        var ok = new SellerStanding("Organizations/a", true, false, KycStatus.Verified, TraderStatus.Trader, true, true, false);

        SparkSellers.SellReasons(ok, options).Should().BeEmpty();
        SparkSellers.SellReasons(ok with { Kyc = KycStatus.Pending }, options).Should().Contain("commerce.seller.kycNotVerified");
        SparkSellers.SellReasons(ok with { TraderVerified = false }, options).Should().Contain("commerce.seller.traderNotVerified");
        SparkSellers.SellReasons(ok with { TraderStatus = TraderStatus.Undeclared }, options).Should().Contain("commerce.seller.traderStatusUndeclared");
        SparkSellers.SellReasons(ok with { Approved = false }, options).Should().Contain("commerce.seller.notApproved");
        // A private seller needs no trader verification.
        SparkSellers.SellReasons(ok with { TraderStatus = TraderStatus.NonTrader, TraderVerified = false }, options).Should().BeEmpty();
    }

    [Fact]
    public void Registration_needs_the_dsa_art30_data_of_a_trader()
    {
        var profile = new SellerProfile { OrganizationId = "Organizations/a", TraderStatus = TraderStatus.Trader };
        SellerRegistration.MissingForSubmit(profile, Options()).Should().Contain("commerce.trader.identityDocument");

        profile.Trader = new TraderData { Name = "A", Address = "B", Phone = "1", Email = "a@b", HasIdentityDocument = true, PaymentAccount = "BE00", SelfCertification = true };
        SellerRegistration.MissingForSubmit(profile, Options()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("-1", "'-1")]
    public void Exports_quote_csv_and_defuse_formulas(string value, string expected)
        => SellerExports.Escape(value).Should().Be(expected);

    [Fact]
    public void The_transparency_report_counts_by_reason_and_restriction_with_the_median_time_to_decision()
    {
        var notices = new List<DsaNotice>
        {
            new() { Reason = DsaReasons.ScamsAndFraud, ReceivedAt = T0, DecidedAt = T0.AddHours(2), Status = DsaNoticeStatus.ActionTaken },
            new() { Reason = DsaReasons.ScamsAndFraud, ReceivedAt = T0, DecidedAt = T0.AddHours(4), Status = DsaNoticeStatus.NoAction },
            new() { Reason = DsaReasons.UnsafeProducts, ReceivedAt = T0, Status = DsaNoticeStatus.Open },
        };
        var statements = new List<DsaStatementOfReasons> { new() { Restriction = DsaRestriction.Removal, BasedOnNotice = true } };

        var report = DsaNotices.Report(T0, T0.AddDays(1), notices, statements);

        report.NoticesReceived.Should().Be(3);
        report.NoticesByReason[DsaReasons.ScamsAndFraud].Should().Be(2);
        report.ActionTaken.Should().Be(1);
        report.Open.Should().Be(1);
        report.MedianHoursToDecision.Should().Be(3d);
        report.StatementsByRestriction["Removal"].Should().Be(1);
    }

    [Fact]
    public void A_statement_of_reasons_carries_every_art17_element()
    {
        var notice = new DsaNotice { Id = "DsaNotices/1", TargetKind = "listing", TargetId = "Products/1", OrganizationId = "Organizations/a" };
        var statement = DsaNotices.BuildStatement(notice,
            new DsaDecisionInput("DsaNotices/1", true, DsaRestriction.DisableAccess, null, ["BE"], "Counterfeit", DsaGround.Illegal, "Reg. 2017/1001 Art. 9"),
            "Users/1", T0);

        statement.Restriction.Should().Be(DsaRestriction.DisableAccess);
        statement.Facts.Should().Be("Counterfeit");
        statement.Ground.Should().Be(DsaGround.Illegal);
        statement.BasedOnNotice.Should().BeTrue();
        statement.Redress.Should().HaveCount(3);
        statement.OrganizationId.Should().Be("Organizations/a");
    }
}
