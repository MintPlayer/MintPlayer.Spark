using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Payments.Stripe;

/// <summary>A Stripe API error (<c>{"error":{"type","code","message","param"}}</c>).</summary>
public sealed class StripeApiException(HttpStatusCode status, string? type, string? code, string? message, string? param)
    : Exception($"Stripe answered {(int)status} {type}/{code}: {message}")
{
    public HttpStatusCode Status { get; } = status;
    public string? Type { get; } = type;
    public string? Code { get; } = code;
    public string? StripeMessage { get; } = message;
    public string? Param { get; } = param;
}

/// <summary>
/// The thin HTTP layer over Stripe's API (<see href="https://docs.stripe.com/api">reference</see>): form-encoded bodies,
/// the platform's secret key, <c>Stripe-Account</c> for a connected account (direct charges, D5),
/// <c>Idempotency-Key</c> on every POST (<see href="https://docs.stripe.com/api/idempotent_requests">idempotent requests</see>)
/// and an optional pinned <c>Stripe-Version</c>. Never logs a credential.
/// </summary>
internal sealed class StripeApi(IHttpClientFactory httpClients, IOptionsMonitor<StripePaymentsOptions> options)
{
    public const string HttpClientName = "spark-commerce-payments-stripe";

    public StripePaymentsOptions Options => options.CurrentValue;

    public Task<JsonElement> GetAsync(string path, MerchantContext merchant, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, path, merchant, null, null, cancellationToken);

    public Task<JsonElement> PostAsync(string path, MerchantContext merchant, StripeForm form, string? idempotencyKey, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, path, merchant, form, idempotencyKey, cancellationToken);

    public async Task<JsonElement> SendAsync(HttpMethod method, string path, MerchantContext merchant, StripeForm? form, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (string.IsNullOrEmpty(current.SecretKey))
            throw new InvalidOperationException("No Stripe key: set Spark:Commerce:Payments:Stripe:SecretKey (user-secrets).");

        using var request = new HttpRequestMessage(method, current.ApiBaseUrl.TrimEnd('/') + "/" + path.TrimStart('/'));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.SecretKey);
        if (merchant.IsConnected)
            request.Headers.TryAddWithoutValidation("Stripe-Account", merchant.AccountId);
        if (current.ApiVersion is { Length: > 0 } version)
            request.Headers.TryAddWithoutValidation("Stripe-Version", version);
        if (idempotencyKey is { Length: > 0 } && method == HttpMethod.Post)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (form is not null)
            request.Content = new FormUrlEncodedContent(form.Fields);

        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            using var ok = JsonDocument.Parse(text);
            return ok.RootElement.Clone();
        }

        string? type = null, code = null, message = null, param = null;
        try
        {
            using var problem = JsonDocument.Parse(text);
            var error = StripeJson.Path(problem.RootElement, "error");
            type = StripeJson.String(error, "type");
            code = StripeJson.String(error, "code");
            message = StripeJson.String(error, "message");
            param = StripeJson.String(error, "param");
        }
        catch (JsonException) { }
        throw new StripeApiException(response.StatusCode, type, code, message, param);
    }
}

/// <summary>A Stripe form body with bracketed keys (<c>line_items[0][price_data][currency]</c>).</summary>
internal sealed class StripeForm
{
    public List<KeyValuePair<string, string>> Fields { get; } = [];

    public StripeForm Add(string key, string? value)
    {
        if (value is not null)
            Fields.Add(new(key, value));
        return this;
    }

    public StripeForm Add(string key, long value) => Add(key, value.ToString(CultureInfo.InvariantCulture));

    public StripeForm Add(string key, bool value) => Add(key, value ? "true" : "false");

    public string? this[string key] => Fields.LastOrDefault(f => f.Key == key).Value;
}

/// <summary>Reading Stripe's JSON. Amounts are integers in the currency's minor unit (<c>1000</c> = €10.00).</summary>
internal static class StripeJson
{
    public static Money? ReadMoney(JsonElement element, string amountProperty, string currencyProperty = "currency")
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(amountProperty, out var amount) && amount.TryGetInt64(out var minor)
            && String(element, currencyProperty) is { } currency
            ? Money.FromMinorUnits(minor, currency.ToUpperInvariant())
            : null;

    public static string? String(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A property that is either an id string or an expanded object with an <c>id</c>.</summary>
    public static string? Id(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => String(value, "id"),
            _ => null,
        };
    }

    public static bool Bool(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    public static long? Long(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : null;

    /// <summary>A Unix-seconds timestamp.</summary>
    public static DateTimeOffset? Time(JsonElement element, string property)
        => Long(element, property) is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    public static JsonElement Path(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return default;
        }
        return current;
    }

    public static IEnumerable<JsonElement> Array(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];
}
