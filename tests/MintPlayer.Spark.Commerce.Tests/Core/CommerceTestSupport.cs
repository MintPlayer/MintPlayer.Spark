using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Tests.Core;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>An options monitor over a fixed value.</summary>
internal sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal static class CommerceFixtures
{
    public static Money Eur(decimal amount) => new(amount, "EUR");

    public static SparkCommerceOptions Options(Action<SparkCommerceOptions>? configure = null)
    {
        var options = new SparkCommerceOptions();
        options.Channels["tickets"] = new CommerceChannelOptions
        {
            Allocation = AllocationMode.HoldFromSelection,
            RequireSignIn = true,
            Fees =
            [
                new FeeRuleOptions { Code = "service", Kind = FeeKind.Service, Fixed = new() { ["EUR"] = 1.50m } },
                new FeeRuleOptions { Code = "booking", Kind = FeeKind.Booking, Scope = FeeScope.PerOrder, Fixed = new() { ["EUR"] = 2.00m } },
                new FeeRuleOptions { Code = "insurance", Kind = FeeKind.Other, Mandatory = false, PercentBasisPoints = 500 },
            ],
        };
        options.Channels["shop"] = new CommerceChannelOptions { Allocation = AllocationMode.OnPayment, SellsGoods = true };
        configure?.Invoke(options);
        return options;
    }
}

/// <summary>A sellable item for tests.</summary>
internal sealed class TestItem : ISellableItem
{
    public string? Id { get; set; } = "Products/1-A";
    public string Channel { get; set; } = "shop";
    public string? OrganizationId { get; set; }
    public Money? Price { get; set; }
    public PriceBasis PriceBasis { get; set; } = PriceBasis.Gross;
    public int? VatRateBasisPoints { get; set; }
}
