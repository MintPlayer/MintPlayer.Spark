using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Commerce.Checkout.Test;

/// <summary>Settings of the test insurer.</summary>
public sealed class TestInsuranceOptions
{
    /// <summary>The premium as basis points of the insured amount. Default 500 (5 %).</summary>
    public int PremiumBasisPoints { get; set; } = 500;

    /// <summary>The smallest premium, in minor units. Default 100 (1.00).</summary>
    public long MinimumPremiumMinorUnits { get; set; } = 100;

    /// <summary>How long a quote stays valid. Default 30 minutes.</summary>
    public TimeSpan QuoteValidity { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Make <c>BindAsync</c> throw (simulate the insurer being down).</summary>
    public bool FailBinding { get; set; }
}

/// <summary>
/// GC4's test adapter: deterministic quotes (the quote id is a hash of the insured lines, so the last step and the
/// placement agree), in-memory policies. Register with <see cref="TestInsuranceExtensions.AddTestInsurance"/>.
/// </summary>
public sealed class TestInsuranceProvider(TestInsuranceOptions options, TimeProvider clock) : IInsuranceProvider
{
    public const string ProviderName = "test";

    private readonly ConcurrentDictionary<string, InsurancePolicy> policies = new();
    private readonly ConcurrentDictionary<string, bool> cancelled = new();

    public string Name => ProviderName;

    /// <summary>The policies bound so far, by order id.</summary>
    public IReadOnlyDictionary<string, InsurancePolicy> Policies => policies;

    /// <summary>The policy ids cancelled so far.</summary>
    public IReadOnlyCollection<string> Cancelled => cancelled.Keys.ToList();

    public Task<InsuranceQuote?> QuoteAsync(InsuranceQuoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InsuredAmount.IsZero)
            return Task.FromResult<InsuranceQuote?>(null);
        var minor = Math.Max(options.MinimumPremiumMinorUnits, Money.RoundDiv(request.InsuredAmount.ToMinorUnits() * options.PremiumBasisPoints, 10_000));
        var premium = Money.FromMinorUnits(minor, request.Currency);
        var key = string.Join("|", request.Lines.Select(l => $"{l.ItemId}:{l.Quantity}:{l.Total.ToMinorUnits()}")) + "|" + request.Currency;
        var quoteId = "test-quote-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Task.FromResult<InsuranceQuote?>(new InsuranceQuote(ProviderName, quoteId, premium, request.InsuredAmount,
            clock.GetUtcNow() + options.QuoteValidity, null, TranslatedString.Of("en", "Test cancellation cover")));
    }

    public Task<InsurancePolicy> BindAsync(InsuranceBindRequest request, CancellationToken cancellationToken)
    {
        if (options.FailBinding)
            throw new InvalidOperationException("The test insurer is down (FailBinding).");
        var policy = policies.GetOrAdd(request.OrderId, _ => new InsurancePolicy(ProviderName, "test-policy-" + Guid.NewGuid().ToString("N")[..12], clock.GetUtcNow()));
        return Task.FromResult(policy);
    }

    public Task CancelAsync(string policyId, CancellationToken cancellationToken)
    {
        cancelled.TryAdd(policyId, true);
        return Task.CompletedTask;
    }
}

public static class TestInsuranceExtensions
{
    /// <summary>Registers the test insurer (GC4). For E2E tests and demos only.</summary>
    public static ISparkBuilder AddTestInsurance(this ISparkBuilder builder, Action<TestInsuranceOptions>? configure = null)
    {
        var options = new TestInsuranceOptions();
        configure?.Invoke(options);
        builder.AddCommerceCheckout();
        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<TestInsuranceProvider>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IInsuranceProvider, TestInsuranceProvider>(sp => sp.GetRequiredService<TestInsuranceProvider>()));
        return builder;
    }
}
