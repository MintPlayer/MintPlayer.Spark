using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Commerce.Retention;
using static MintPlayer.Spark.Commerce.Tests.Core.CommerceFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Core;

/// <summary>PRD §4.2: the refusals and warnings of the commerce startup validation.</summary>
public class CommerceStartupValidationTests
{
    private sealed record TestProvider(string Provider, bool SupportsSplit) : ICommercePaymentCapabilities;

    private static CommerceValidationResult Evaluate(
        SparkCommerceOptions options,
        IReadOnlyList<ICommercePaymentCapabilities>? payments = null,
        IEnumerable<CountryKeyedRule>? rules = null,
        IReadOnlyList<SparkConsentRequirement>? unmetConsent = null,
        bool hasKyc = true)
        => CommerceStartupValidation.Evaluate(options, payments ?? [], rules ?? [],
            CommerceRetention.Build(options, [new CommerceRetentionFloors()]), unmetConsent ?? [], hasKyc);

    [Fact]
    public void The_default_scenario_starts()
        => Evaluate(Options()).Errors.Should().BeEmpty();

    [Fact]
    public void Open_registration_with_the_platform_as_merchant_is_refused_unless_acknowledged()
    {
        Evaluate(Options(o => o.Sellers.Registration = SellerRegistrationMode.Open)).Errors.Should().ContainSingle();
        Evaluate(Options(o => { o.Sellers.Registration = SellerRegistrationMode.Open; o.Sellers.AcknowledgeOpenPlatformMerchant = true; }))
            .Errors.Should().BeEmpty();
    }

    [Fact]
    public void A_randomized_pre_queue_without_sign_in_is_refused_unless_overridden()
    {
        var options = Options(o =>
        {
            o.Channels["tickets"].RequireSignIn = false;
            o.Channels["tickets"].WaitingRoom.Modes = [WaitingRoomMode.RandomizedPreQueue];
        });
        Evaluate(options).Errors.Should().ContainSingle();

        options.Channels["tickets"].WaitingRoom.AllowWithoutSignIn = true;
        Evaluate(options).Errors.Should().BeEmpty();
    }

    [Fact]
    public void A_deceptive_measure_without_its_trigger_is_refused()
        => Evaluate(Options(o => o.Channels["tickets"].DeceptiveMeasures["tarpit"] = new DeceptiveMeasureOptions { Enabled = true }))
            .Errors.Should().ContainSingle();

    [Fact]
    public void A_per_buyer_limit_in_a_guest_channel_is_a_warning()
    {
        var result = Evaluate(Options(o => o.Channels["shop"].PerBuyerLimit = 2));
        result.Errors.Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Contains("PerBuyerLimit"));
    }

    /// <summary>D41 (owner, 2026-10-19): "Allow via split, opt-in".</summary>
    [Fact]
    public void Mixing_sellers_needs_the_merchant_of_record_opt_in_and_a_provider_that_splits()
    {
        var optedIn = Options(o => { o.Merchant = MerchantMode.ConnectedSellers; o.Basket.MixSellers = true; o.Basket.AcknowledgeSplitMerchantOfRecord = true; });
        var notOptedIn = Options(o => { o.Merchant = MerchantMode.ConnectedSellers; o.Basket.MixSellers = true; });

        Evaluate(optedIn, [new TestProvider("stripe", true)]).Errors.Should().BeEmpty();
        // One split-capable provider is enough; the other's methods are never offered to a mixed basket (warned).
        var mixed = Evaluate(optedIn, [new TestProvider("mollie", true), new TestProvider("test", false)]);
        mixed.Errors.Should().BeEmpty();
        mixed.Warnings.Should().Contain(w => w.Contains("'test' cannot split"));
        Evaluate(optedIn, [new TestProvider("test", false)]).Errors.Should().ContainSingle(e => e.Contains("none of the payment providers"));

        Evaluate(notOptedIn, [new TestProvider("stripe", true)]).Errors.Should().ContainSingle(e => e.Contains("AcknowledgeSplitMerchantOfRecord"));
        Evaluate(notOptedIn, [new TestProvider("test", false)]).Errors.Should().HaveCount(2);
    }

    [Fact]
    public void Mixing_sellers_is_off_by_default()
    {
        var defaults = new SparkCommerceOptions();
        defaults.Basket.MixSellers.Should().BeFalse();
        defaults.Basket.AcknowledgeSplitMerchantOfRecord.Should().BeFalse();
    }

    [Theory]
    [InlineData(CountryKey.Nationality, CountryRuleEffect.Refuse)]
    [InlineData(CountryKey.Residence, CountryRuleEffect.DifferentConditions)]
    [InlineData(CountryKey.CardIssuingCountry, CountryRuleEffect.Refuse)]
    [InlineData(CountryKey.IpCountry, CountryRuleEffect.Redirect)]
    [InlineData(CountryKey.Residence, CountryRuleEffect.RiskSignal)]
    public void Geo_blocking_forbidden_rules_are_refused(CountryKey key, CountryRuleEffect effect)
        => Evaluate(Options(), rules: [new CountryKeyedRule("test", key, effect, ["NL"])]).Errors.Should().ContainSingle();

    [Fact]
    public void Delivery_zones_keyed_on_the_delivery_address_stay_allowed()
        => Evaluate(Options(o => o.CountryRules.Add(new CountryRuleOptions { Name = "zones", Key = CountryKey.DeliveryAddress, Effect = CountryRuleEffect.Refuse, Countries = ["US"] })))
            .Errors.Should().BeEmpty();

    [Fact]
    public void A_consent_requiring_feature_without_the_consent_primitive_is_refused()
        => Evaluate(Options(), unmetConsent: [new SparkConsentRequirement(SparkConsentPurposes.ThirdPartyChallenge, "Challenges.Turnstile")])
            .Errors.Should().ContainSingle(e => e.Contains("third-party-challenge"));

    [Fact]
    public void Switching_trader_traceability_off_needs_the_art29_acknowledgement()
    {
        Evaluate(Options(o => o.Sellers.TraderTraceability = false)).Errors.Should().ContainSingle();
        Evaluate(Options(o => { o.Sellers.TraderTraceability = false; o.Sellers.AcknowledgeDsaArt29Exemption = true; })).Errors.Should().BeEmpty();
    }

    [Fact]
    public void A_retention_period_below_its_legal_floor_is_refused()
    {
        Evaluate(Options(o => o.Retention.Periods[CommerceDocumentTypes.Invoices] = TimeSpan.FromDays(365))).Errors.Should().ContainSingle();
        Evaluate(Options(o => o.Retention.Periods[CommerceDocumentTypes.Invoices] = TimeSpan.FromDays(4000))).Errors.Should().BeEmpty();
    }

    [Fact]
    public void A_host_confirmed_floor_replaces_the_built_in_one()
    {
        var options = Options(o =>
        {
            o.Country = "NL";
            o.Retention.ConfirmedFloors[CommerceDocumentTypes.Invoices] = TimeSpan.FromDays(2557);
            o.Retention.Periods[CommerceDocumentTypes.Invoices] = TimeSpan.FromDays(2600);
        });
        var result = Evaluate(options);
        result.Errors.Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Contains("overridden"));
    }

    [Fact]
    public void Duplicate_fee_codes_are_refused()
        => Evaluate(Options(o => o.Channels["shop"].Fees =
            [new FeeRuleOptions { Code = "a", PercentBasisPoints = 1 }, new FeeRuleOptions { Code = "A", PercentBasisPoints = 2 }]))
            .Errors.Should().ContainSingle();

    [Fact]
    public void Invalid_hold_bounds_are_refused()
        => Evaluate(Options(o => o.Channels["tickets"].Hold = new DurationBounds { Default = TimeSpan.FromMinutes(30), Min = TimeSpan.FromMinutes(5), Max = TimeSpan.FromMinutes(20) }))
            .Errors.Should().ContainSingle();

    [Fact]
    public void Connected_sellers_without_a_kyc_source_is_a_warning()
        => Evaluate(Options(o => o.Merchant = MerchantMode.ConnectedSellers), hasKyc: false)
            .Warnings.Should().Contain(w => w.Contains("KYC"));

    [Fact]
    public void Seller_choices_are_clamped_into_the_channel_bounds_when_read()
    {
        var hold = Options().Channels["tickets"].Hold;
        hold.Clamp(null).Should().Be(TimeSpan.FromMinutes(10));
        hold.Clamp(TimeSpan.FromMinutes(1)).Should().Be(TimeSpan.FromMinutes(5));
        hold.Clamp(TimeSpan.FromHours(1)).Should().Be(TimeSpan.FromMinutes(20));
    }
}
