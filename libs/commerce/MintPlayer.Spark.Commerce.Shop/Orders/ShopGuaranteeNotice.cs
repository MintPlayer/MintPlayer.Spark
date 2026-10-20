using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.MailManager;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Shop.Orders;

/// <summary>
/// GA8 on the order confirmation (the durable medium of CRD Art. 8(7)): an attachment <c>guarantee-information.html</c>
/// with the legal-guarantee notice and, per product, any commercial guarantee of durability its producer gives. Added
/// through Checkout's <see cref="ICheckoutMailAttachments"/>, so Checkout never references Shop.
/// <para><b>Legal basis.</b> The legal guarantee: Directive (EU) 2019/771 Art. 10(1) (the seller is liable for a lack of
/// conformity that exists at delivery and becomes apparent within two years; member states may set longer periods,
/// Art. 10(3)). The information duty: CRD Art. 6(1)(l) (the harmonised notice) and, for a commercial guarantee of durability,
/// Art. 6(1)(la) (the harmonised label), as amended and inserted by the ECGT Directive (EU) 2024/825 Art. 2, applying
/// from 27 September 2026. Commission Implementing Regulation (EU) 2025/1960 fixes both (applies from 27 September 2026;
/// "None of the elements of the harmonised notice can be edited"). ⚠️ The default below is the library's own wording,
/// not that notice: a host replaces it through <c>GuaranteeNotice</c>. Checked against EUR-Lex 02011L0083-20260927,
/// 02019L0771-20260731 and 32025R1960 on 2026-10-20; the national period (<c>LegalGuaranteeMonths</c>, default 24,
/// Art. 10(3)) was not checked.</para>
/// </summary>
internal sealed class ShopGuaranteeNotice(
    IDocumentStore store,
    IOptionsMonitor<SparkCommerceShopOptions> options) : ICheckoutMailAttachments
{
    public async Task<IReadOnlyList<SparkMailAttachment>> GetAsync(CheckoutMailContext context, CancellationToken cancellationToken)
    {
        if (context.Kind != CheckoutMailKind.Confirmation)
            return [];
        var lines = context.Order.Lines.Where(l => l.IsPhysicalGoods && ShopItemIds.Handles(l.ItemId) && l.Status == OrderLineStatus.Active).ToList();
        if (lines.Count == 0)
            return [];
        using var session = store.OpenAsyncSession();
        var variantIds = lines.Select(l => ShopItemIds.VariantId(l.ItemId)!).Distinct(StringComparer.Ordinal).ToList();
        var variants = await session.Include<ShopVariant>(v => v.ProductId).LoadAsync<ShopVariant>(variantIds, cancellationToken);
        var products = await session.LoadAsync<ShopProduct>(variants.Values.OfType<ShopVariant>().Select(v => v.ProductId).Distinct(StringComparer.Ordinal), cancellationToken);
        var html = Html(context.Language, options.CurrentValue, products.Values.OfType<ShopProduct>().ToList());
        return [SparkMailAttachment.FromBytes("guarantee-information.html", "text/html", Encoding.UTF8.GetBytes(html))];
    }

    /// <summary>The notice texts by language: the host's (<c>GuaranteeNotice</c>) or the library's default. Pure.</summary>
    internal static string NoticeText(string language, SparkCommerceShopOptions options)
    {
        var lang = (language ?? "en").Split('-')[0].ToLowerInvariant();
        if (options.GuaranteeNotice is { Count: > 0 } custom)
            return custom.TryGetValue(lang, out var own) ? own : custom.TryGetValue("en", out var en) ? en : custom.Values.First();
        var months = options.LegalGuaranteeMonths;
        return lang switch
        {
            "nl" => $"Wettelijke garantie: als consument hebt u recht op een wettelijke garantie van ten minste {months} maanden vanaf de levering voor elk conformiteitsgebrek van de goederen (Richtlijn (EU) 2019/771). U kunt dan kosteloos herstel of vervanging vragen, en anders een prijsvermindering of de ontbinding van de overeenkomst. Deze garantie geldt naast elke commerciële garantie.",
            "fr" => $"Garantie légale : en tant que consommateur, vous bénéficiez d'une garantie légale d'au moins {months} mois à compter de la livraison pour tout défaut de conformité des biens (directive (UE) 2019/771). Vous pouvez alors demander gratuitement la réparation ou le remplacement, à défaut une réduction du prix ou la résolution du contrat. Cette garantie s'applique en plus de toute garantie commerciale.",
            _ => $"Legal guarantee: as a consumer you have a legal guarantee of at least {months} months from delivery for any lack of conformity of the goods (Directive (EU) 2019/771). You can then ask for repair or replacement free of charge, or else a price reduction or termination of the contract. This guarantee applies in addition to any commercial guarantee.",
        };
    }

    private static string DurabilityLine(string language, ShopProduct product, string name)
    {
        var months = product.DurabilityGuaranteeMonths!.Value;
        var by = product.DurabilityGuarantor is { Length: > 0 } g ? g : null;
        return (language ?? "en").Split('-')[0].ToLowerInvariant() switch
        {
            "nl" => $"{name}: commerciële duurzaamheidsgarantie van {months} maanden{(by is null ? "" : " door " + by)}.",
            "fr" => $"{name} : garantie commerciale de durabilité de {months} mois{(by is null ? "" : " par " + by)}.",
            _ => $"{name}: commercial guarantee of durability of {months} months{(by is null ? "" : " by " + by)}.",
        };
    }

    /// <summary>The attachment's HTML: every seller and host text is HTML-encoded. Pure.</summary>
    internal static string Html(string language, SparkCommerceShopOptions options, IReadOnlyList<ShopProduct> products)
    {
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html lang=\"").Append(WebUtility.HtmlEncode(language)).Append("\"><head><meta charset=\"utf-8\"><title>Guarantee</title></head><body>");
        builder.Append("<p>").Append(WebUtility.HtmlEncode(NoticeText(language, options))).Append("</p>");
        foreach (var product in products.Where(p => p.DurabilityGuaranteeMonths is > 0).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var name = product.Name.GetValue(language) ?? product.Name.GetValue(TranslatedString.DefaultLanguage) ?? product.Slug;
            builder.Append("<p>").Append(WebUtility.HtmlEncode(DurabilityLine(language, product, name)));
            if (product.DurabilityGuaranteeTerms?.GetValue(language) is { Length: > 0 } terms)
                builder.Append("<br>").Append(WebUtility.HtmlEncode(terms));
            builder.Append("</p>");
        }
        builder.Append("</body></html>");
        return builder.ToString();
    }
}
