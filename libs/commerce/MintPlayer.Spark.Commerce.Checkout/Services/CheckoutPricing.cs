using System.Globalization;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Checkout.Services;

/// <summary>A line to price: the resolved item and the quantity.</summary>
internal sealed record PricingInput(string LineId, CheckoutItem Item, int Quantity);

/// <summary>
/// Prices a basket: FinalPrice per line (§4.4, defined once in <see cref="IFinalPriceCalculator"/>), per-order fees
/// counted once per channel, then every <see cref="ICheckoutPricingStep"/> by order.
/// </summary>
internal sealed class CheckoutPricing(
    IFinalPriceCalculator calculator,
    IOptionsMonitor<SparkCommerceOptions> commerce,
    IEnumerable<ICheckoutPricingStep> steps)
{
    public async Task<CheckoutPricingContext> PriceAsync(
        IReadOnlyList<PricingInput> lines, IReadOnlyDictionary<string, string> choices, string? buyerUserId, bool placing, CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
            throw CheckoutException.Of(CheckoutRefusals.BasketEmpty);
        var currency = lines[0].Item.Price.Currency;
        if (lines.Any(l => l.Item.Price.Currency != currency))
            throw CheckoutException.Of(CheckoutRefusals.CurrencyMismatch);

        var priced = new List<PricedLine>(lines.Count);
        var perOrderSeen = new HashSet<(string Channel, string Code)>();
        foreach (var line in lines)
        {
            var price = calculator.Calculate(new FinalPriceRequest(line.Item.Channel, line.Item.Price, line.Item.PriceBasis,
                line.Item.VatRateBasisPoints, line.Quantity));
            price = CountPerOrderFeesOnce(price, line.Item.Channel, perOrderSeen);
            priced.Add(new PricedLine { LineId = line.LineId, Item = line.Item, Quantity = line.Quantity, Price = price });
        }

        var context = new CheckoutPricingContext
        {
            Currency = currency,
            Lines = priced,
            Choices = choices,
            BuyerUserId = buyerUserId,
            Placing = placing,
        };
        foreach (var step in steps.OrderBy(s => s.Order))
            await step.ApplyAsync(context, cancellationToken);

        if (context.Total.ToMinorUnits() < 0)
            throw CheckoutException.Of(CheckoutRefusals.NegativeTotal);
        return context;
    }

    /// <summary>A channel's per-order fee is charged once per order, not once per line of that channel.</summary>
    private FinalPrice CountPerOrderFeesOnce(FinalPrice price, string channel, HashSet<(string, string)> seen)
    {
        if (!commerce.CurrentValue.Channels.TryGetValue(channel, out var options))
            return price;
        var perOrder = options.Fees.Where(f => f.Scope == FeeScope.PerOrder).Select(f => f.Code).ToHashSet(StringComparer.Ordinal);
        if (perOrder.Count == 0)
            return price;

        var removed = new List<PriceComponent>();
        foreach (var fee in price.MandatoryFees.Where(f => perOrder.Contains(f.Code)))
        {
            if (!seen.Add((channel, fee.Code)))
                removed.Add(fee);
        }
        if (removed.Count == 0)
            return price;
        var removedTotal = Money.Sum(removed.Select(f => f.Amount), price.Total.Currency);
        return price with
        {
            MandatoryFees = price.MandatoryFees.Except(removed).ToList(),
            Total = price.Total - removedTotal,
        };
    }
}

/// <summary>
/// GC3: a donation line to the host's beneficiary, from the buyer's choice <c>donation</c>: one of the configured fixed
/// amounts, or <c>roundup</c> (the total rounded up to the next whole unit). VAT category "outside scope" by default.
/// Refunded only with the whole order (a refused line never refunds it).
/// </summary>
internal sealed class DonationPricingStep(IOptionsMonitor<SparkCommerceCheckoutOptions> options) : ICheckoutPricingStep
{
    public const string ChoiceKey = "donation";
    public const string RoundUpChoice = "roundup";

    public int Order => 900;

    public Task ApplyAsync(CheckoutPricingContext context, CancellationToken cancellationToken)
    {
        var donation = options.CurrentValue.Donation;
        if (!donation.Enabled || !context.Choices.TryGetValue(ChoiceKey, out var choice) || string.IsNullOrWhiteSpace(choice))
            return Task.CompletedTask;

        Money amount;
        if (string.Equals(choice, RoundUpChoice, StringComparison.OrdinalIgnoreCase))
        {
            if (!donation.RoundUp)
                throw CheckoutException.Of(CheckoutRefusals.DonationInvalid);
            var total = context.Total.Amount;
            amount = new Money(Math.Ceiling(total) - total, context.Currency);
        }
        else
        {
            if (!decimal.TryParse(choice, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                || value <= 0 || value > donation.MaxAmount
                || !donation.FixedAmounts.TryGetValue(context.Currency, out var allowed) || !allowed.Contains(value))
                throw CheckoutException.Of(CheckoutRefusals.DonationInvalid);
            amount = new Money(value, context.Currency);
        }

        if (!amount.IsZero)
            context.Extras.Add(new PricedExtra("donation", PricedExtraKind.Donation, amount, "commerceCheckout.donation", "checkout",
                donation.VatCategory, donation.BeneficiaryOrganizationId));
        return Task.CompletedTask;
    }
}

/// <summary>
/// GC4: an optional cancellation cover, added only when the buyer chose it (choice <c>insurance=accept</c>): never
/// pre-ticked, quoted after the main price (the step runs last).
/// </summary>
internal sealed class InsurancePricingStep(CheckoutInsurance insurance) : ICheckoutPricingStep
{
    public const string ChoiceKey = "insurance";
    public const string AcceptChoice = "accept";

    public int Order => 950;

    public async Task ApplyAsync(CheckoutPricingContext context, CancellationToken cancellationToken)
    {
        if (!context.Choices.TryGetValue(ChoiceKey, out var choice) || !string.Equals(choice, AcceptChoice, StringComparison.OrdinalIgnoreCase))
            return;
        var quote = await insurance.QuoteAsync(context, cancellationToken)
            ?? throw CheckoutException.Of(CheckoutRefusals.InsuranceUnavailable);
        context.Extras.Add(new PricedExtra("insurance", PricedExtraKind.Insurance, quote.Premium, "commerceCheckout.insurance", "checkout:" + quote.Provider,
            "insurance", quote.QuoteId));
    }
}

/// <summary>The configured insurance provider (GC4), if any.</summary>
internal sealed class CheckoutInsurance(IEnumerable<IInsuranceProvider> providers, IOptionsMonitor<SparkCommerceCheckoutOptions> options)
{
    public IInsuranceProvider? Provider
    {
        get
        {
            var all = providers.ToList();
            var name = options.CurrentValue.Insurance.Provider;
            if (name is { Length: > 0 })
                return all.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            return all.Count == 1 ? all[0] : null;
        }
    }

    /// <summary>A quote for the insurable lines (those of the configured channels), excluding donations and other extras.</summary>
    public Task<InsuranceQuote?> QuoteAsync(CheckoutPricingContext context, CancellationToken cancellationToken)
    {
        if (Provider is not { } provider)
            return Task.FromResult<InsuranceQuote?>(null);
        var channels = options.CurrentValue.Insurance.Channels;
        var lines = context.Lines.Where(l => channels.Count == 0 || channels.Contains(l.Item.Channel, StringComparer.OrdinalIgnoreCase)).ToList();
        if (lines.Count == 0)
            return Task.FromResult<InsuranceQuote?>(null);
        var insured = Money.Sum(lines.Select(l => l.Total), context.Currency);
        return provider.QuoteAsync(new InsuranceQuoteRequest(context.Currency, insured,
            lines.Select(l => new InsuranceQuoteLine(l.Item.ItemId, l.Item.Channel, l.Quantity, l.Total, l.Item.ServiceDate)).ToList()), cancellationToken);
    }
}
