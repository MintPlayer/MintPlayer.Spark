using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Claims;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Invoicing;
using MintPlayer.Spark.Commerce.Invoicing.EInvoicing;
using MintPlayer.Spark.Commerce.Invoicing.Exports;
using MintPlayer.Spark.Commerce.Invoicing.Peppol.Test;
using MintPlayer.Spark.Commerce.Invoicing.Pdf;
using MintPlayer.Spark.Commerce.Invoicing.Reference;
using MintPlayer.Spark.Commerce.Invoicing.Services;
using MintPlayer.Spark.Commerce.Invoicing.Vat;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Tests.Payments;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Tests.Invoicing;

/// <summary>The invoicing fixtures (Invoicing/Fixtures, copied to the output). See Fixtures/README.md.</summary>
internal static class InvoicingFixtures
{
    public static string Text(string folder, string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Invoicing", "Fixtures", folder, name));
}

/// <summary>A VIES stand-in: the listed ids are valid, every other is not (and VIES was reached).</summary>
internal sealed class FakeVatIds(params string[] valid) : IVatIdValidator
{
    public List<string> Checked { get; } = [];

    public Task<VatIdCheckResult> CheckAsync(VatId vatId, CancellationToken cancellationToken)
    {
        Checked.Add(vatId.ToString());
        return Task.FromResult(valid.Contains(vatId.ToString())
            ? new VatIdCheckResult(true, true, "Example BV", null, "WAPI-TEST-1")
            : new VatIdCheckResult(false, true));
    }
}

/// <summary>Event venues for admission lines (Art. 53/54): item id → country.</summary>
internal sealed class FakeLineFacts : IInvoiceLineFactsSource
{
    public Dictionary<string, InvoiceLineFacts> Facts { get; } = new(StringComparer.Ordinal);

    public Task<InvoiceLineFacts?> GetAsync(CheckoutOrder order, CheckoutOrderLine line, CancellationToken cancellationToken)
        => Task.FromResult(Facts.TryGetValue(line.ItemId, out var f) ? f : null);
}

/// <summary>Invoicing over a real RavenDB store, without the web host.</summary>
internal sealed class InvoicingHost
{
    public required IServiceProvider Services { get; init; }
    public required ManualTimeProvider Clock { get; init; }
    public required SparkCommerceInvoicingOptions Options { get; init; }
    public required SparkCommerceOptions Commerce { get; init; }
    public required FakeVatIds VatIds { get; init; }
    public required FakeLineFacts Facts { get; init; }
    public required TestPeppolAccessPoint Peppol { get; init; }
    public required TestPeppolOptions PeppolOptions { get; init; }

    public T Get<T>() where T : notnull => Services.CreateScope().ServiceProvider.GetRequiredService<T>();

    public ISparkInvoicing Invoicing => Get<ISparkInvoicing>();

    public static SparkCommerceInvoicingOptions DefaultOptions()
    {
        var o = new SparkCommerceInvoicingOptions
        {
            Platform = new InvoiceIssuerOptions
            {
                Name = "Example Platform BV", VatId = "BE0123456749", CompanyId = "0123456749", Country = "BE",
                AddressLines = ["Voorbeeldstraat 1"], PostalCode = "9000", City = "Gent", Email = "billing@example.org",
            },
            TimeZone = "Europe/Brussels",
        };
        o.VatCategories["standard"] = new VatCategoryOptions { Rates = { ["BE"] = 2100, ["NL"] = 2100, ["DE"] = 1900, ["FR"] = 2000, ["ES"] = 2100 } };
        o.VatCategories["admission"] = new VatCategoryOptions { Kind = VatCategoryKind.Admission, IsService = true, Rates = { ["BE"] = 600, ["NL"] = 900, ["DE"] = 700, ["FR"] = 550 } };
        o.VatCategories["service"] = new VatCategoryOptions { IsService = true, Rates = { ["BE"] = 2100, ["NL"] = 2100, ["DE"] = 1900 } };
        o.VatCategories["donation"] = new VatCategoryOptions { Kind = VatCategoryKind.OutsideScope, ExemptionReason = "Donation, no consideration" };
        o.Territories.Add(new VatTerritoryOptions { Country = "ES", Code = "ES-CN", PostcodePattern = "^(35|38)\\d{3}$", LegalBasis = "Directive 2006/112/EC Art. 6(1)(b)" });
        return o;
    }

    public static InvoicingHost Build(IDocumentStore store, Action<SparkCommerceInvoicingOptions>? configure = null, Action<SparkCommerceOptions>? commerce = null, params string[] validVatIds)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-19T10:00:00Z"));
        var options = DefaultOptions();
        configure?.Invoke(options);
        var commerceOptions = new SparkCommerceOptions();
        commerce?.Invoke(commerceOptions);
        var vatIds = new FakeVatIds(validVatIds);
        var facts = new FakeLineFacts();
        var peppolOptions = new TestPeppolOptions();
        var peppol = new TestPeppolAccessPoint(peppolOptions, clock);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<ISparkClaims>(new SparkClaims(store, clock));
        services.AddSingleton<IOptionsMonitor<SparkCommerceInvoicingOptions>>(new StaticOptionsMonitor<SparkCommerceInvoicingOptions>(options));
        services.AddSingleton<IOptionsMonitor<SparkCommerceOptions>>(new StaticOptionsMonitor<SparkCommerceOptions>(commerceOptions));
        services.AddSingleton<IOptionsMonitor<SparkCommercePaymentsOptions>>(new StaticOptionsMonitor<SparkCommercePaymentsOptions>(new SparkCommercePaymentsOptions { PublicBaseUrl = "https://shop.example" }));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<IVatIdValidator>(vatIds);
        services.AddSingleton<IInvoiceLineFactsSource>(facts);
        services.AddSingleton<IVatReferenceSource, NoVatReferenceSource>();
        foreach (var rule in BuiltInPlaceOfSupplyRules.All)
            services.AddSingleton(typeof(IPlaceOfSupplyRule), rule);
        services.AddSingleton<IEInvoiceValidator, En16931RuleSubset>();
        services.AddSingleton(peppol);
        services.AddSingleton<IPeppolAccessPoint>(peppol);
        services.AddSingleton<IInvoicePdfRenderer, InvoicePdfRenderer>();
        services.AddScoped<InvoiceNumbering>();
        services.AddScoped<InvoiceIssuers>();
        services.AddScoped<VatTerritories>();
        services.AddScoped<InvoiceLinks>();
        services.AddScoped<EInvoiceDispatcher>();
        services.AddScoped<InvoiceAfterIssue>();
        services.AddScoped<InvoicingExports>();
        services.AddScoped<LegalDocumentPdfs>();
        services.AddScoped<InvoicingService>();
        services.AddScoped<ISparkInvoicing>(sp => sp.GetRequiredService<InvoicingService>());

        return new InvoicingHost
        {
            Services = services.BuildServiceProvider(), Clock = clock, Options = options, Commerce = commerceOptions, VatIds = vatIds,
            Facts = facts, Peppol = peppol, PeppolOptions = peppolOptions,
        };
    }

    /// <summary>A confirmed order with one line per (item, gross, quantity, goods?).</summary>
    public static CheckoutOrder Order(string id, params (string Item, decimal Gross, int Quantity, bool Goods, string? Category)[] lines)
    {
        var order = new CheckoutOrder
        {
            Id = id, Number = id.Replace("CheckoutOrders/", "", StringComparison.Ordinal), State = OrderState.Confirmed, Currency = "EUR",
            BuyerEmail = "buyer@example.org", BuyerName = "A. Buyer", BuyerUserId = "users/1",
            CreatedAt = DateTimeOffset.Parse("2026-10-19T09:00:00Z"), ConfirmedAt = DateTimeOffset.Parse("2026-10-19T09:05:00Z"),
        };
        var n = 0;
        foreach (var (item, gross, quantity, goods, category) in lines)
        {
            var total = new Money(gross, "EUR");
            order.Lines.Add(new CheckoutOrderLine
            {
                LineId = "l" + (++n), ItemId = item, Title = TranslatedString.Of("en", item), Channel = goods ? "shop" : "tickets",
                Quantity = quantity, UnitPrice = Money.FromMinorUnits(total.ToMinorUnits() / quantity, "EUR"), Total = total, VatCategory = category, IsPhysicalGoods = goods,
                Status = OrderLineStatus.Active,
            });
        }
        order.Total = Money.Sum(order.Lines.Select(l => l.Total), "EUR");
        return order;
    }
}
