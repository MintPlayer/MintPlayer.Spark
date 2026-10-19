using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Commerce.Shipping.Sendcloud;

/// <summary>
/// The thin HTTP layer over Sendcloud's documented API (<see href="https://sendcloud.dev/api/v2/parcels/create-a-parcel-or-parcels">parcels</see>,
/// <see href="https://sendcloud.dev/api/v2/service-points/retrieve-a-list-of-service-points">service points</see>). Authenticates
/// with HTTP Basic (public key, secret key) of the shipment's account, and never logs a credential. Credentials go only
/// to Sendcloud's own hosts: a label URL on another host is refused.
/// </summary>
internal sealed class SendcloudApi(IHttpClientFactory httpClients, IOptionsMonitor<SendcloudOptions> options)
{
    public const string HttpClientName = "spark-commerce-shipping-sendcloud";

    public SendcloudOptions Options => options.CurrentValue;

    public Task<JsonElement> GetAsync(string url, string? account, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, url, null, account, cancellationToken);

    public Task<JsonElement> PostAsync(string url, JsonObject? body, string? account, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, url, body, account, cancellationToken);

    public async Task<JsonElement> SendAsync(HttpMethod method, string url, JsonObject? body, string? account, CancellationToken cancellationToken)
    {
        using var request = Request(method, url, account);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new CarrierException(SendcloudCarrier.CarrierName, ErrorMessage(text, response.StatusCode), (int)response.StatusCode);
        if (string.IsNullOrWhiteSpace(text))
            return default;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>A POST answering only its status: <c>cancel</c> answers 200 (cancelled), 202 (queued) or 400/404/410 (cannot).</summary>
    public async Task<HttpStatusCode> PostStatusAsync(string url, string? account, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, url, account);
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        return response.StatusCode;
    }

    /// <summary>Downloads a document (label, CN23) from a Sendcloud URL with the account's credentials.</summary>
    public async Task<(byte[] Content, string MediaType)> DownloadAsync(string url, string? account, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, url, account);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new CarrierException(SendcloudCarrier.CarrierName, $"the document download answered {(int)response.StatusCode}.", (int)response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return (bytes, response.Content.Headers.ContentType?.MediaType ?? CarrierDocument.Pdf);
    }

    public string Api(string relative) => Absolute(Options.ApiBaseUrl, relative);

    public string ServicePoints(string relative) => Absolute(Options.ServicePointsBaseUrl, relative);

    private HttpRequestMessage Request(HttpMethod method, string url, string? account)
    {
        var current = options.CurrentValue;
        if (!IsSendcloudUrl(url, current))
            throw new CarrierException(SendcloudCarrier.CarrierName, "refused to send credentials to a URL outside Sendcloud's API hosts.");
        var (publicKey, secretKey) = Credentials(current, account);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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

    /// <summary>Only the configured API hosts receive credentials (label URLs come from Sendcloud's own responses).</summary>
    internal static bool IsSendcloudUrl(string url, SendcloudOptions options)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme is not ("https" or "http"))
            return false;
        return new[] { options.ApiBaseUrl, options.ServicePointsBaseUrl }
            .Select(b => Uri.TryCreate(b, UriKind.Absolute, out var u) ? u : null)
            .Any(b => b is not null && b.Scheme == target.Scheme && string.Equals(b.Host, target.Host, StringComparison.OrdinalIgnoreCase) && b.Port == target.Port);
    }

    private static string Absolute(string baseUrl, string relative)
        => baseUrl.TrimEnd('/') + "/" + relative.TrimStart('/');

    /// <summary>Sendcloud's error shape: <c>{"error":{"code":400,"message":"…"}}</c>.</summary>
    private static string ErrorMessage(string body, HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
                return $"{(int)status} {message.GetString()}";
        }
        catch (JsonException) { }
        return $"answered {(int)status}.";
    }
}
