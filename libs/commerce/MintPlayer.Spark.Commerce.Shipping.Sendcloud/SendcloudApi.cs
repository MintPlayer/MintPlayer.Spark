using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Commerce.Shipping.Sendcloud;

/// <summary>
/// The thin HTTP layer over Sendcloud's documented API v3 (<see href="https://sendcloud.dev/api/v3/shipments">shipments</see>,
/// <see href="https://sendcloud.dev/api/v3/returns">returns</see>,
/// <see href="https://sendcloud.dev/api/v3/parcel-documents/retrieve-a-parcel-document">parcel documents</see>,
/// <see href="https://sendcloud.dev/api/v3/service-points/retrieve-a-list-of-service-points">service points</see>).
/// Authenticates with HTTP Basic (public key, secret key) of the shipment's account, and never logs a credential.
/// Credentials go only to the configured API host: a document link on another host is refused.
/// </summary>
internal sealed class SendcloudApi(IHttpClientFactory httpClients, IOptionsMonitor<SendcloudOptions> options)
{
    public const string HttpClientName = "spark-commerce-shipping-sendcloud";

    public SendcloudOptions Options => options.CurrentValue;

    public async Task<JsonElement> GetAsync(string url, string? account, CancellationToken cancellationToken)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, url, null, account, cancellationToken);
        return IsSuccess(status) ? body : throw Error(status, body);
    }

    public async Task<JsonElement> PostAsync(string url, JsonObject? body, string? account, CancellationToken cancellationToken)
    {
        var (status, answer) = await SendAsync(HttpMethod.Post, url, body, account, cancellationToken);
        return IsSuccess(status) ? answer : throw Error(status, answer);
    }

    /// <summary>Any answer with its status, for calls whose non-2xx answers carry meaning (409 on announce, 404 on a lookup).</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string url, JsonObject? body, string? account, CancellationToken cancellationToken)
    {
        using var request = Request(method, url, account);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
            return (response.StatusCode, default);
        try
        {
            using var document = JsonDocument.Parse(text);
            return (response.StatusCode, document.RootElement.Clone());
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, default);
        }
    }

    /// <summary>
    /// Downloads a parcel document (<c>GET /parcels/{id}/documents/{type}</c>) in <paramref name="mediaType"/>
    /// (<c>application/pdf</c>, <c>application/zpl</c>, <c>image/png</c>). Answers null on 404 (the parcel has no such document).
    /// </summary>
    public async Task<(byte[] Content, string MediaType)?> DownloadAsync(string url, string mediaType, string? account, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, url, account);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new CarrierException(SendcloudCarrier.CarrierName, $"the document download answered {(int)response.StatusCode}.", (int)response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        // A label the carrier makes natively in ZPL is always answered as ZPL, whatever was asked.
        return (bytes, response.Content.Headers.ContentType?.MediaType ?? mediaType);
    }

    public string Api(string relative) => Options.ApiBaseUrl.TrimEnd('/') + "/" + relative.TrimStart('/');

    private HttpRequestMessage Request(HttpMethod method, string url, string? account)
    {
        var current = options.CurrentValue;
        if (!IsSendcloudUrl(url, current))
            throw new CarrierException(SendcloudCarrier.CarrierName, "refused to send credentials to a URL outside Sendcloud's API host.");
        var (publicKey, secretKey) = Credentials(current, account);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}")));
        return request;
    }

    internal static (string PublicKey, string SecretKey) Credentials(SendcloudOptions options, string? account)
    {
        if (account is { Length: > 0 })
        {
            if (!options.Accounts.TryGetValue(account, out var named) || named.PublicKey is not { Length: > 0 } p || named.SecretKey is not { Length: > 0 } s)
                throw new CarrierException(SendcloudCarrier.CarrierName, $"no keys for account '{account}': set Spark:Commerce:Shipping:Sendcloud:Accounts:{account} (user-secrets).");
            return (p, s);
        }
        if (options.PublicKey is not { Length: > 0 } publicKey || options.SecretKey is not { Length: > 0 } secretKey)
            throw new CarrierException(SendcloudCarrier.CarrierName, "no keys: set Spark:Commerce:Shipping:Sendcloud:PublicKey and SecretKey (user-secrets).");
        return (publicKey, secretKey);
    }

    /// <summary>Only the configured API host receives credentials (document links come from Sendcloud's own answers).</summary>
    internal static bool IsSendcloudUrl(string url, SendcloudOptions options)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme is not ("https" or "http"))
            return false;
        return Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var api)
            && api.Scheme == target.Scheme
            && string.Equals(api.Host, target.Host, StringComparison.OrdinalIgnoreCase)
            && api.Port == target.Port;
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    internal static CarrierException Error(HttpStatusCode status, JsonElement body)
        => new(SendcloudCarrier.CarrierName, ErrorMessage(body, status), (int)status);

    /// <summary>
    /// v3's error shape: <c>{"errors":[{"status":"400","code":"required","detail":"…","source":{"pointer":"/to_address/name"}}]}</c>.
    /// Older endpoints answer <c>{"error":{"code":…,"message":"…"}}</c> or <c>{"detail":"…"}</c>.
    /// </summary>
    internal static string ErrorMessage(JsonElement body, HttpStatusCode status)
    {
        var parts = new List<string>();
        if (body.ValueKind == JsonValueKind.Object)
        {
            if (body.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray())
                {
                    var detail = error.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    var pointer = error.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object
                        && source.TryGetProperty("pointer", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                    var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    parts.Add(string.Join(" ", new[] { pointer, detail ?? code }.Where(s => s is { Length: > 0 })));
                }
            }
            else if (body.TryGetProperty("error", out var legacy) && legacy.ValueKind == JsonValueKind.Object && legacy.TryGetProperty("message", out var message))
                parts.Add(message.GetString() ?? string.Empty);
            else if (body.TryGetProperty("detail", out var single) && single.ValueKind == JsonValueKind.String)
                parts.Add(single.GetString() ?? string.Empty);
        }
        var text = string.Join("; ", parts.Where(p => p.Length > 0));
        if (text.Length > 500)
            text = text[..500];
        return text.Length > 0 ? $"{(int)status} {text}" : $"answered {(int)status}.";
    }
}
