using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Payments.Services;

/// <summary>The registered providers, onboarding providers and settlement sources, by name.</summary>
internal sealed class PaymentProviderRegistry(
    IEnumerable<IPaymentProvider> providers,
    IEnumerable<IConnectOnboardingProvider> onboarding,
    IEnumerable<ISettlementSource> settlements,
    IOptionsMonitor<SparkCommercePaymentsOptions> options)
{
    private readonly Dictionary<string, IPaymentProvider> byName = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IPaymentProvider> All => byName.Values;

    /// <summary>The configured provider, or the only one registered.</summary>
    public string DefaultName
        => options.CurrentValue.Provider is { Length: > 0 } configured
            ? configured
            : byName.Count == 1
                ? byName.Keys.Single()
                : throw new InvalidOperationException("Several payment providers are registered: set Spark:Commerce:Payments:Provider.");

    public IPaymentProvider Get(string? name)
    {
        var key = name is { Length: > 0 } ? name : DefaultName;
        return byName.TryGetValue(key, out var provider)
            ? provider
            : throw new InvalidOperationException($"No payment provider '{key}' is registered.");
    }

    public IConnectOnboardingProvider? Onboarding(string provider)
        => onboarding.FirstOrDefault(o => string.Equals(o.Provider, provider, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ISettlementSource> SettlementSources => settlements;

    /// <summary>The catalog of a provider with the host's overrides.</summary>
    public IReadOnlyList<PaymentMethodInfo> Methods(IPaymentProvider provider)
    {
        options.CurrentValue.Methods.TryGetValue(provider.Name, out var overrides);
        return PaymentMethodCatalog.WithOverrides(provider.Methods, overrides);
    }
}

/// <summary>The webhook source names of the payment adapters: <c>payments-{provider}</c>, served at <c>/spark/webhooks/payments-{provider}</c>.</summary>
public static class PaymentWebhookSources
{
    /// <summary>The source a provider's payment webhooks arrive on.</summary>
    public static string For(string provider) => "payments-" + provider.ToLowerInvariant();

    /// <summary>The provider of a source name, or null when it is not a payments source.</summary>
    public static string? ProviderOf(string source)
    {
        if (!source.StartsWith("payments-", StringComparison.Ordinal))
            return null;
        var rest = source["payments-".Length..];
        // payments-stripe-connect carries Stripe's Connect events (its own signing secret).
        var dash = rest.IndexOf('-');
        return dash < 0 ? rest : rest[..dash];
    }
}

/// <summary>Absolute URLs for redirects and webhooks.</summary>
internal sealed class PaymentUrls(
    IOptionsMonitor<SparkCommercePaymentsOptions> options,
    IServiceProvider services)
{
    public string? BaseUrl
        => SparkPublicBaseUrl.Resolve(
            options.CurrentValue.PublicBaseUrl,
            services.GetService<IConfiguration>(),
            services.GetService<IHostEnvironment>(),
            services.GetService<IHttpContextAccessor>()?.HttpContext);

    /// <summary><paramref name="url"/> made absolute when it is app-relative.</summary>
    public string Absolute(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp))
            return url;
        var origin = BaseUrl ?? throw new InvalidOperationException("No public base URL: set Spark:Commerce:Payments:PublicBaseUrl.");
        return origin.TrimEnd('/') + "/" + url.TrimStart('/');
    }

    /// <summary>The webhook URL of a provider's source.</summary>
    public string Webhook(string provider) => Absolute("/spark/webhooks/" + PaymentWebhookSources.For(provider));
}

/// <summary>
/// Encrypts Connect OAuth tokens at rest with ASP.NET Core Data Protection (purpose-bound), so a database dump or
/// backup does not hand out access to sellers' accounts. The key ring is the host's (it must be persisted and shared
/// between instances, as for the antiforgery and visitor cookies).
/// </summary>
internal sealed class ConnectTokenProtector(IDataProtectionProvider dataProtection)
{
    private readonly IDataProtector protector = dataProtection.CreateProtector("MintPlayer.Spark.Commerce.Payments.ConnectTokens.v1");

    public string Protect(ConnectTokens tokens)
        => protector.Protect(JsonSerializer.Serialize(new Stored(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt)));

    public ConnectTokens? Unprotect(string? protectedTokens)
    {
        if (string.IsNullOrEmpty(protectedTokens))
            return null;
        var stored = JsonSerializer.Deserialize<Stored>(protector.Unprotect(protectedTokens));
        return stored is null ? null : new ConnectTokens(stored.A, stored.R, stored.E);
    }

    private sealed record Stored(string A, string? R, DateTimeOffset E);
}
