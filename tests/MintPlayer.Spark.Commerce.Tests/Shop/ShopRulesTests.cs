using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Shipping;
using MintPlayer.Spark.Commerce.Shop;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Contracts;
using MintPlayer.Spark.Commerce.Shop.Feeds;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Commerce.Shop.Orders;
using MintPlayer.Spark.Commerce.Shop.Reviews;
using MintPlayer.Spark.Commerce.Shop.Services;

namespace MintPlayer.Spark.Commerce.Tests.Shop;

/// <summary>
/// The shop's pure rules (M12): identifiers, the unit price (GA3), GPSR information (GA7), the guarantee notice (GA8),
/// the standard delivery cost of a withdrawal (GA1), refund terms, review verification (GC5), feed formatting (GC6),
/// catalogue search words and startup validation.
/// </summary>
public class ShopRulesTests
{
    internal static ShopProduct Goods(Action<ShopProduct>? configure = null)
    {
        var product = new ShopProduct
        {
            Id = "ShopProducts/1-A",
            Name = TranslatedString.Of("en", "Olive oil"),
            Slug = "olive-oil",
            Channel = "shop",
            Kind = EShopProductKind.Goods,
            Price = new Money(9.30m, "EUR"),
            ManufacturerName = "Oliva SA",
            ManufacturerAddress = "Rue 1, 1000 Bruxelles",
            ManufacturerContact = "info@oliva.example",
            ManufacturerCountry = "BE",
            SafetyIdentifier = "OL-75",
            SafetyPictureUrl = "/img/oil.jpg",
        };
        configure?.Invoke(product);
        return product;
    }

    internal static ShopVariant Variant(string id = "ShopVariants/1-A", Action<ShopVariant>? configure = null)
    {
        var variant = new ShopVariant { Id = id, ProductId = "ShopProducts/1-A", Sku = "OIL-75", Capacity = 10 };
        configure?.Invoke(variant);
        return variant;
    }

    private static SparkCommerceOptions Commerce()
    {
        var options = new SparkCommerceOptions();
        options.Channels["shop"] = new CommerceChannelOptions();
        return options;
    }

    // ---- identifiers --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("4006381333931", true)]   // a valid EAN-13 (GS1 check digit)
    [InlineData("4006381333932", false)]  // wrong check digit
    [InlineData("96385074", true)]        // a valid GTIN-8
    [InlineData("400638133393", false)]   // GTIN-12 shape with a wrong check digit
    [InlineData("40063813339A1", false)]
    [InlineData("12345", false)]
    public void A_GTIN_needs_a_valid_GS1_check_digit(string gtin, bool valid)
        => ProductRules.IsGtin(gtin).Should().Be(valid);

    [Theory]
    [InlineData("TSHIRT-M/RED", true)]
    [InlineData("a.b_c", true)]
    [InlineData("-leading", false)]
    [InlineData("with space", false)]
    public void A_SKU_is_letters_digits_and_a_few_separators(string sku, bool valid)
        => ProductRules.IsSku(sku).Should().Be(valid);

    [Theory]
    [InlineData("620520", true)]
    [InlineData("62052000", true)]
    [InlineData("6205200010", true)]
    [InlineData("6205", false)]
    [InlineData("62052A", false)]
    public void An_HS_code_has_6_8_or_10_digits(string code, bool valid)
        => ProductRules.IsHsCode(code).Should().Be(valid);

    [Fact]
    public void A_slug_is_made_from_the_name_without_accents()
        => ShopSlugs.Slugify("Crème brûlée — 250 g!").Should().Be("creme-brulee-250-g");

    // ---- GA3: unit price ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_unit_price_is_computed_from_the_FinalPrice_and_the_net_quantity_GA3()
    {
        var perLitre = ProductRules.UnitPrice(new Money(9.30m, "EUR"), EUnitOfMeasure.Litre, 0.75m);
        perLitre.Should().Be(new Money(12.40m, "EUR"));
    }

    [Fact]
    public void The_unit_price_may_refer_to_100_grams_GA3()
    {
        // 3.00 for 250 g, shown per 100 g (reference 0.1 kg).
        ProductRules.UnitPrice(new Money(3.00m, "EUR"), EUnitOfMeasure.Kilogram, 0.25m, 0.1m).Should().Be(new Money(1.20m, "EUR"));
    }

    [Fact]
    public void The_unit_price_rounds_once_half_away_from_zero_GA3()
    {
        // 1.00 for 0.3 kg = 3.333… per kg → 3.33.
        ProductRules.UnitPrice(new Money(1.00m, "EUR"), EUnitOfMeasure.Kilogram, 0.3m).Should().Be(new Money(3.33m, "EUR"));
    }

    [Fact]
    public void No_unit_price_without_a_unit_of_measure_or_a_net_quantity()
    {
        (ProductRules.UnitPrice(new Money(1m, "EUR"), EUnitOfMeasure.None, 1m) is null).Should().BeTrue();
        (ProductRules.UnitPrice(new Money(1m, "EUR"), EUnitOfMeasure.Litre, null) is null).Should().BeTrue();
        (ProductRules.UnitPrice(new Money(1m, "EUR"), EUnitOfMeasure.Litre, 0m) is null).Should().BeTrue();
    }

    [Fact]
    public void A_product_sold_by_quantity_needs_a_net_quantity_to_be_published_GA3()
    {
        var product = Goods(p => p.UnitOfMeasure = EUnitOfMeasure.Litre);
        ProductRules.Problems(product, [Variant()], Commerce()).Should().Contain(p => p.Contains("net quantity"));
        product.NetQuantity = 0.75m;
        ProductRules.Problems(product, [Variant()], Commerce()).Should().BeEmpty();
    }

    // ---- GA7: GPSR --------------------------------------------------------------------------------------------------

    [Fact]
    public void Goods_without_manufacturer_data_cannot_be_published_GA7()
    {
        var product = Goods(p => { p.ManufacturerName = null; p.ManufacturerContact = null; });
        var problems = ProductRules.SafetyProblems(product, [Variant()]);
        problems.Should().Contain(p => p.Contains("Art. 19(a)") && p.Contains("name"));
        problems.Should().Contain(p => p.Contains("electronic address"));
    }

    [Fact]
    public void A_manufacturer_outside_the_EU_needs_an_EU_responsible_person_GA7()
    {
        var product = Goods(p => p.ManufacturerCountry = "CN");
        ProductRules.SafetyProblems(product, [Variant()]).Should().ContainSingle().Which.Should().Contain("Art. 19(b)");
        product.ResponsiblePersonName = "EU Rep BV";
        product.ResponsiblePersonAddress = "Kade 1, Rotterdam";
        product.ResponsiblePersonContact = "rep@example.eu";
        ProductRules.SafetyProblems(product, [Variant()]).Should().BeEmpty();
    }

    [Fact]
    public void A_GTIN_on_a_variant_identifies_the_product_and_a_gallery_image_is_its_picture_GA7()
    {
        var product = Goods(p => { p.SafetyIdentifier = null; p.SafetyPictureUrl = null; });
        ProductRules.SafetyProblems(product, [Variant()]).Should().HaveCount(2);
        product.Media.Add(new ShopProductMedia { Url = "/img/a.jpg", Alt = TranslatedString.Of("en", "Bottle") });
        ProductRules.SafetyProblems(product, [Variant(configure: v => v.Gtin = "4006381333931")]).Should().BeEmpty();
    }

    [Fact]
    public void A_service_needs_no_safety_information_GA7()
        => ProductRules.SafetyProblems(Goods(p => { p.Kind = EShopProductKind.Service; p.ManufacturerName = null; }), [Variant()]).Should().BeEmpty();

    [Fact]
    public void Every_image_needs_an_alternative_text_GB13()
    {
        var product = Goods(p => p.Media.Add(new ShopProductMedia { Url = "/img/a.jpg" }));
        ProductRules.Problems(product, [Variant()], Commerce()).Should().Contain(p => p.Contains("alternative text"));
    }

    [Fact]
    public void Facet_terms_come_from_the_brand_the_attributes_and_the_active_variants_options_GB13()
    {
        var product = Goods(p => { p.Brand = "Oliva"; p.Attributes.Add(new ShopProductAttribute { Name = "origin", Value = "Crete" }); });
        var red = Variant("v1", v => v.Options.Add(new ShopVariantOption { Name = "colour", Value = "Red " }));
        var blue = Variant("v2", v => { v.IsActive = false; v.Options.Add(new ShopVariantOption { Name = "colour", Value = "Blue" }); });
        ProductRules.FacetValues(product, [red, blue]).Should().Equal("brand:oliva", "colour:red", "origin:crete");
    }

    // ---- GA8: guarantee notice ------------------------------------------------------------------------------------

    [Fact]
    public void The_guarantee_attachment_states_the_legal_guarantee_and_the_durability_guarantee_GA8()
    {
        var product = Goods(p => { p.DurabilityGuaranteeMonths = 36; p.DurabilityGuarantor = "Oliva <SA>"; });
        var html = ShopGuaranteeNotice.Html("en", new SparkCommerceShopOptions(), [product]);
        html.Should().Contain("at least 24 months");
        html.Should().Contain("Directive (EU) 2019/771");
        html.Should().Contain("36 months");
        html.Should().Contain("Oliva &lt;SA&gt;");
        html.Should().NotContain("<SA>");
    }

    [Fact]
    public void A_host_replaces_the_notice_per_language_GA8()
    {
        var options = new SparkCommerceShopOptions { GuaranteeNotice = new() { ["en"] = "Harmonised notice", ["nl"] = "Geharmoniseerde melding" } };
        ShopGuaranteeNotice.NoticeText("nl-BE", options).Should().Be("Geharmoniseerde melding");
        ShopGuaranteeNotice.NoticeText("fr", options).Should().Be("Harmonised notice");
        ShopGuaranteeNotice.NoticeText("nl", new SparkCommerceShopOptions()).Should().Contain("Wettelijke garantie");
        SparkShopCatalog.GuaranteeNotice(options)!.GetValue("nl").Should().Be("Geharmoniseerde melding");
        SparkShopCatalog.GuaranteeNotice(new SparkCommerceShopOptions()).Should().BeNull();
    }

    [Fact]
    public void Startup_refuses_a_legal_guarantee_under_two_years()
    {
        var options = new SparkCommerceShopOptions { LegalGuaranteeMonths = 12 };
        SparkCommerceShopExtensions.Problems(options, Commerce()).Should().Contain(p => p.Contains("LegalGuaranteeMonths"));
        SparkCommerceShopExtensions.Problems(new SparkCommerceShopOptions(), Commerce()).Should().BeEmpty();
    }

    [Fact]
    public void Startup_refuses_an_unconfigured_channel_and_a_page_path_without_slug()
    {
        var options = new SparkCommerceShopOptions { DefaultChannel = "goods", ProductPagePath = "/shop/products" };
        var problems = SparkCommerceShopExtensions.Problems(options, Commerce());
        problems.Should().Contain(p => p.Contains("DefaultChannel"));
        problems.Should().Contain(p => p.Contains("ProductPagePath"));
    }

    // ---- GA1: standard delivery and refund terms --------------------------------------------------------------------

    private static ShippingRate Rate(string method, decimal price, ShippingMethodKind kind = ShippingMethodKind.Carrier)
        => new() { Method = method, Kind = kind, Price = new Money(price, "EUR") };

    [Fact]
    public void The_standard_delivery_is_the_cheapest_carrier_method_never_more_than_paid_GA1()
    {
        var rates = new[] { Rate("express", 12.95m), Rate("standard", 4.95m), Rate("pickup", 0m, ShippingMethodKind.Pickup) };
        // The buyer chose express: a withdrawal refunds 4.95 of it (CRD Art. 13(2)); collecting in person is not a delivery.
        ShopOrders.StandardDelivery(rates, new Money(12.95m, "EUR")).Should().Be(new Money(4.95m, "EUR"));
        // A free delivery (threshold reached) refunds nothing more than was paid.
        ShopOrders.StandardDelivery(rates, new Money(0m, "EUR")).Should().Be(new Money(0m, "EUR"));
        // Without carrier rates, all of the delivery counts as standard.
        ShopOrders.StandardDelivery([Rate("pickup", 0m, ShippingMethodKind.Pickup)], new Money(6m, "EUR")).Should().Be(new Money(6m, "EUR"));
    }

    [Fact]
    public void A_product_names_its_withdrawal_exemption_and_its_seller_policy()
    {
        var terms = ShopRefundTermsSource.Terms(Goods(p => { p.WithdrawalExemption = "16(e)"; p.RefundPolicy = RefundPolicyKind.WithFee; p.RefundFeeBasisPoints = 1000; }));
        terms.WithdrawalRight.HasValue.Should().BeTrue();
        terms.WithdrawalRight!.Value.Should().BeFalse();
        terms.WithdrawalExemption.Should().Be("16(e)");
        terms.Policy!.Kind.Should().Be(RefundPolicyKind.WithFee);

        // Goods without an exemption leave the right to Checkout (goods carry it).
        ShopRefundTermsSource.Terms(Goods()).WithdrawalRight.HasValue.Should().BeFalse();
    }

    // ---- the capacity source ----------------------------------------------------------------------------------------

    [Fact]
    public void A_variant_sells_only_while_its_product_is_published_and_claims_its_own_quota()
    {
        var product = Goods(p => p.IsPublished = true);
        var variant = Variant();
        var request = new global::MintPlayer.Spark.Commerce.Checkout.CheckoutItemRequest(ShopItemIds.For(variant.Id!), 2);
        var item = ShopCapacitySource.Resolve(new ShopSellable(product, variant), request, Commerce());
        item.Should().NotBeNull();
        item!.IsPhysicalGoods.Should().BeTrue();
        item.Capacity.Quotas.Should().ContainSingle().Which.Should().Be(new global::MintPlayer.Spark.Commerce.Inventory.QuotaLine(ShopQuotas.Variant(variant.Id!), 2));

        product.IsPublished = false;
        ShopCapacitySource.Resolve(new ShopSellable(product, variant), request, Commerce()).Should().BeNull();
    }

    // ---- GC5: reviews --------------------------------------------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_a_delivered_line_within_the_window_is_verified_GC5()
    {
        var window = TimeSpan.FromDays(365);
        ShopReviews.IsVerified(new ShopOrderLine { DeliveredAt = Now.AddDays(-3) }, Now, window).Should().BeTrue();
        ShopReviews.IsVerified(new ShopOrderLine { DeliveredAt = null, ShippedQuantity = 1 }, Now, window).Should().BeFalse();
        ShopReviews.IsVerified(new ShopOrderLine { DeliveredAt = Now.AddDays(-400) }, Now, window).Should().BeFalse();
        ShopReviews.IsVerified(new ShopOrderLine { DeliveredAt = Now.AddDays(-3), Cancelled = true }, Now, window).Should().BeFalse();
        ShopReviews.IsVerified(null, Now, window).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, "Fine", "rating")]
    [InlineData(6, "Fine", "rating")]
    [InlineData(5, "   ", "body")]
    [InlineData(1, "Bad, but a review all the same", null)]
    public void A_review_needs_a_rating_from_1_to_5_and_a_text_GC5(int rating, string body, string? problem)
        => ShopReviews.Problem(new SparkShopReviewRequest("SO-1", "L1", rating, body), 4000).Should().Be(problem);

    [Fact]
    public void The_summary_rounds_the_average_to_one_decimal()
    {
        ShopReviews.Summary([5, 4, 4]).Should().Be((3, (decimal?)4.3m));
        ShopReviews.Summary([]).Average.HasValue.Should().BeFalse();
    }

    // ---- GC6: feeds --------------------------------------------------------------------------------------------------

    private static ShopFeedItem FeedItem(string? gtin = "4006381333931", ShopAvailability availability = ShopAvailability.InStock)
        => ShopFeeds.Item(
            Goods(p => { p.Brand = "Oliva"; p.UnitOfMeasure = EUnitOfMeasure.Litre; p.NetQuantity = 0.75m; p.Media.Add(new ShopProductMedia { Url = "/img/a.jpg", Alt = TranslatedString.Of("en", "Bottle") }); }),
            Variant(configure: v => v.Gtin = gtin),
            new Money(9.30m, "EUR"),
            availability,
            "https://shop.example",
            new SparkCommerceShopOptions());

    [Fact]
    public void A_feed_item_links_the_product_page_with_absolute_URLs_GC6()
    {
        var item = FeedItem();
        item.Link.Should().Be("https://shop.example/shop/products/olive-oil?variant=ShopVariants%2F1-A");
        item.ImageLink.Should().Be("https://shop.example/img/a.jpg");
        item.UnitPricingMeasure.Should().Be("0.75 l");
        item.UnitPricingBaseMeasure.Should().Be("1 l");
        ShopFeeds.Price(item.Price).Should().Be("9.30 EUR");
    }

    [Fact]
    public void The_Google_feed_uses_the_g_namespace_and_says_when_no_identifier_exists_GC6()
    {
        var xml = ShopFeeds.GoogleXml([FeedItem(), FeedItem(gtin: null, ShopAvailability.OutOfStock) with { Id = "OIL-1L" }], "https://shop.example");
        xml.Should().Contain("xmlns:g=\"http://base.google.com/ns/1.0\"");
        xml.Should().Contain("<g:gtin>4006381333931</g:gtin>");
        xml.Should().Contain("<g:availability>out_of_stock</g:availability>");
        xml.Should().Contain("<g:identifier_exists>no</g:identifier_exists>");
        xml.Should().Contain("<g:price>9.30 EUR</g:price>");
        xml.Should().StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
    }

    [Fact]
    public void The_Meta_feed_is_RFC_4180_CSV_safe_against_formula_injection_GC6()
    {
        var csv = ShopFeeds.MetaCsv([FeedItem() with { Title = "=HYPERLINK(\"x\")", Description = "Line, with comma" }]);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be(string.Join(",", ShopFeeds.MetaColumns));
        lines[1].Should().Contain("\"'=HYPERLINK(\"\"x\"\")\"");
        lines[1].Should().Contain("\"Line, with comma\"");
        lines[1].Should().Contain("in stock");
        ShopFeeds.Cell("-1").Should().Be("'-1");
        ShopFeeds.Cell(null).Should().Be("");
    }

    // ---- catalogue search -------------------------------------------------------------------------------------------

    [Fact]
    public void Search_words_are_letters_and_digits_only_at_most_eight()
    {
        CatalogSearch.Words("Olive-oil  \"extra\" OR 1=1; *").Should().Equal("olive", "oil", "extra", "or", "1");
        CatalogSearch.Words(string.Join(' ', Enumerable.Range(1, 20).Select(i => "w" + i))).Should().HaveCount(8);
    }

    [Fact]
    public void Typo_tolerance_follows_cores_rule()
    {
        CatalogSearch.Similarity(3).HasValue.Should().BeFalse();
        CatalogSearch.Similarity(5)!.Value.Should().BeLessThan(1m);
        CatalogSearch.Similarity(8)!.Value.Should().BeLessThan(CatalogSearch.Similarity(7)!.Value);
    }

    [Theory]
    [InlineData("colour:red", true)]
    [InlineData("Colour:red", false)]
    [InlineData("colour:", false)]
    [InlineData(":red", false)]
    public void A_facet_term_is_a_key_and_a_value(string term, bool valid)
        => CatalogSearch.IsFacetTerm(term).Should().Be(valid);
}
