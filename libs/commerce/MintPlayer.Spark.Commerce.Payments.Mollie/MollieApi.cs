using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Payments.Mollie;

/// <summary>A Mollie API error (<c>application/hal+json</c> problem: <c>status</c>, <c>title</c>, <c>detail</c>, <c>field</c>).</summary>
public sealed class MollieApiException(HttpStatusCode status, string? title, string? detail, string? field)
    : Exception($"Mollie answered {(int)status} {title}: {detail}")
{
    public HttpStatusCode Status { get; } = status;
    public string? Title { get; } = title;
    public string? Detail { get; } = detail;
    public string? Field { get; } = field;
}

/// <summary>
/// The thin HTTP layer over Mollie's v2 API (<see href="https://docs.mollie.com/reference/overview">reference</see>).
/// Authenticates with the platform's API key, or with a seller's OAuth access token (Mollie Connect), adds
/// <c>testmode</c> where OAuth needs it, sends the <c>Idempotency-Key</c> header on POSTs
/// (<see href="https://docs.mollie.com/reference/api-idempotency">API idempotency</see>), and never logs a credential.
/// </summary>
internal sealed class MollieApi(IHttpClientFactory httpClients, IOptionsMonitor<MolliePaymentsOptions> options)
{
    public const string HttpClientName = "spark-commerce-payments-mollie";

    public MolliePaymentsOptions Options => options.CurrentValue;

    public Task<JsonElement> GetAsync(string path, MerchantContext merchant, CancellationToken cancellationToken, string? bearer = null)
        => SendAsync(HttpMethod.Get, path, merchant, null, null, cancellationToken, bearer);

    public Task<JsonElement> PostAsync(string path, MerchantContext merchant, JsonObject body, string? idempotencyKey, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, path, merchant, body, idempotencyKey, cancellationToken);

    public Task<JsonElement> DeleteAsync(string path, MerchantContext merchant, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Delete, path, merchant, null, null, cancellationToken);

    public async Task<JsonElement> SendAsync(HttpMethod method, string path, MerchantContext merchant, JsonObject? body, string? idempotencyKey, CancellationToken cancellationToken, string? bearer = null)
    {
        var current = options.CurrentValue;
        var oauth = merchant.AccessToken is { Length: > 0 };
        var token = bearer ?? (oauth ? merchant.AccessToken : current.ApiKey);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("No Mollie credential: set Spark:Commerce:Payments:Mollie:ApiKey (user-secrets), or link the seller's account.");

        var testMode = oauth && (merchant.TestMode || current.TestMode);
        var url = Absolute(path, current.ApiBaseUrl);
        if (testMode && method != HttpMethod.Post)
            url += (url.Contains('?') ? "&" : "?") + "testmode=true";
        if (testMode && body is not null)
            body["testmode"] = true;

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/hal+json"));
        if (idempotencyKey is { Length: > 0 })
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    /// <summary>A form POST to the OAuth token endpoint, with the app's client credentials (HTTP Basic).</summary>
    public async Task<JsonElement> TokenAsync(IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (string.IsNullOrEmpty(current.ClientId) || string.IsNullOrEmpty(current.ClientSecret))
            throw new InvalidOperationException("Mollie Connect needs Spark:Commerce:Payments:Mollie:ClientId and ClientSecret (user-secrets).");

        using var request = new HttpRequestMessage(HttpMethod.Post, current.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{current.ClientId}:{current.ClientSecret}")));
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    private static string Absolute(string path, string baseUrl)
        => Uri.TryCreate(path, UriKind.Absolute, out _) ? path : baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            if (string.IsNullOrWhiteSpace(text))
                return default;
            using var ok = JsonDocument.Parse(text);
            return ok.RootElement.Clone();
        }

        string? title = null, detail = null, field = null;
        try
        {
            using var problem = JsonDocument.Parse(text);
            title = MollieJson.String(problem.RootElement, "title");
            detail = MollieJson.String(problem.RootElement, "detail") ?? MollieJson.String(problem.RootElement, "error_description");
            field = MollieJson.String(problem.RootElement, "field");
        }
        catch (JsonException) { }
        throw new MollieApiException(response.StatusCode, title ?? response.ReasonPhrase, detail, field);
    }
}

/// <summary>Reading and writing Mollie's JSON.</summary>
internal static class MollieJson
{
    /// <summary>Mollie's amount object: <c>{"currency":"EUR","value":"10.00"}</c>, the value with exactly the currency's decimals.</summary>
    public static JsonObject Amount(Money money)
        => new()
        {
            ["currency"] = money.Currency,
            ["value"] = money.Amount.ToString("F" + Money.MinorUnitDigits(money.Currency).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
        };

    public static Money? ReadMoney(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var amount) ? ReadMoney(amount) : null;

    public static Money? ReadMoney(JsonElement amount)
    {
        if (amount.ValueKind != JsonValueKind.Object
            || String(amount, "currency") is not { } currency
            || String(amount, "value") is not { } value
            || !decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
            return null;
        return new Money(parsed, currency);
    }

    public static string? String(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static bool Bool(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    public static DateTimeOffset? Date(JsonElement element, string property)
        => String(element, property) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

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

    /// <summary>The items of an embedded list: <c>_embedded.{name}</c>.</summary>
    public static IEnumerable<JsonElement> Embedded(JsonElement element, string name)
    {
        var list = Path(element, "_embedded", name);
        return list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];
    }

    /// <summary>The next page's URL (<c>_links.next.href</c>), or null.</summary>
    public static string? Next(JsonElement element) => String(Path(element, "_links", "next"), "href");
}
