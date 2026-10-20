using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Commerce.Invoicing.EInvoicing;

namespace MintPlayer.Spark.Commerce.Invoicing.Peppol.Storecove;

/// <summary>
/// <c>Spark:Commerce:Invoicing:Peppol:Storecove</c>. The API key comes from configuration or user-secrets only, is read
/// per call (a rotated key takes effect without a restart) and is never logged.
/// </summary>
public sealed class StorecoveOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Invoicing:Peppol:Storecove";

    /// <summary>The API key (<c>Authorization: Bearer …</c>).</summary>
    public string? ApiKey { get; set; }

    /// <summary>The Storecove legal entity that sends for the platform (and for any issuer without its own).</summary>
    public long? LegalEntityId { get; set; }

    /// <summary>Legal entities per issuer key (a seller's organization id) in connected mode: one Storecove legal entity per seller.</summary>
    public Dictionary<string, long> LegalEntities { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The API base. Storecove's sandbox uses the same host with a sandbox API key.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.storecove.com/api/v2/";
}

/// <summary>
/// Storecove (<see href="https://www.storecove.com/docs/"/>): <c>POST document_submissions</c> with the UBL as
/// <c>rawDocumentData</c> (base64, <c>parseStrategy: ubl</c>, Peppol BIS Billing 3 process id) routed to the receiver's
/// Peppol identifier, and <c>POST discovery/receives</c> to check the receiver. Storecove names identifier schemes its own
/// way (<c>BE:EN</c> for ICD 0208); the map below covers the common ones and is overridable. An idempotency GUID derived
/// from our document id makes a retried submission land once.
/// </summary>
/// <remarks>
/// ⚠️ Doc-derived (round 4, D37): the request and response shapes follow Storecove's public API reference; they were not
/// replayed against the sandbox. Fixtures in <c>tests/…/Invoicing/Fixtures/Storecove</c> are marked as such.
/// </remarks>
internal sealed class StorecoveAccessPoint(IHttpClientFactory httpClients, IOptionsMonitor<StorecoveOptions> options) : IPeppolAccessPoint
{
    public const string ProviderName = "storecove";
    public const string HttpClientName = "spark-commerce-invoicing-storecove";

    /// <summary>ISO 6523 ICD → Storecove scheme (Storecove's "receiver identifiers" list).</summary>
    internal static readonly Dictionary<string, string> Schemes = new(StringComparer.Ordinal)
    {
        ["0208"] = "BE:EN", ["9925"] = "BE:VAT", ["0106"] = "NL:KVK", ["0190"] = "NL:OINO", ["9944"] = "NL:VAT",
        ["0204"] = "DE:LWID", ["9930"] = "DE:VAT", ["0009"] = "FR:SIRET", ["0002"] = "FR:SIRENE", ["9957"] = "FR:VAT",
        ["0007"] = "SE:ORGNR", ["0192"] = "NO:ORG", ["0184"] = "DK:DIGST", ["0088"] = "GLN", ["0060"] = "DUNS",
        ["0211"] = "IT:IVA", ["9906"] = "IT:VAT", ["0037"] = "FI:OVT", ["9938"] = "LU:VAT", ["0151"] = "AU:ABN",
    };

    public string Name => ProviderName;

    public async Task<PeppolLookupResult> LookupAsync(PeppolParticipant receiver, string documentTypeId, CancellationToken cancellationToken)
    {
        if (!Schemes.TryGetValue(receiver.Scheme, out var scheme))
            return new PeppolLookupResult(false, $"Storecove has no scheme for ICD {receiver.Scheme}.");
        var body = new JsonObject
        {
            ["documentTypes"] = new JsonArray("invoice"),
            ["network"] = "peppol",
            ["metaScheme"] = "iso6523-actorid-upis",
            ["scheme"] = scheme,
            ["identifier"] = receiver.Identifier,
        };
        var (status, json) = await SendAsync(HttpMethod.Post, "discovery/receives", body, cancellationToken);
        if (status is HttpStatusCode.OK && json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var code))
            return new PeppolLookupResult(code.GetString() == "OK", code.GetString() == "OK" ? null : "Storecove: " + code.GetString());
        throw new PeppolException(ProviderName, $"discovery answered {(int)status}.", (int)status);
    }

    public async Task<PeppolSendResult> SendAsync(PeppolSendRequest request, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var legalEntity = settings.LegalEntities.TryGetValue(request.IssuerKey, out var own) ? own : settings.LegalEntityId;
        if (legalEntity is null)
            return new PeppolSendResult(false, null, $"No Storecove legal entity for issuer '{request.IssuerKey}'.");
        if (!Schemes.TryGetValue(request.Receiver.Scheme, out var scheme))
            return new PeppolSendResult(false, null, $"Storecove has no scheme for ICD {request.Receiver.Scheme}.");

        var body = BuildSubmission(request, legalEntity.Value, scheme);
        var (status, json) = await SendAsync(HttpMethod.Post, "document_submissions", body, cancellationToken);
        if (status is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted)
            return new PeppolSendResult(true, json.ValueKind == JsonValueKind.Object && json.TryGetProperty("guid", out var guid) ? guid.GetString() : null);
        var retryable = status is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
        return new PeppolSendResult(false, null, $"Storecove answered {(int)status}: {Errors(json)}", retryable);
    }

    /// <summary>The submission body (doc-derived shape).</summary>
    internal static JsonObject BuildSubmission(PeppolSendRequest request, long legalEntityId, string scheme) => new()
    {
        ["legalEntityId"] = legalEntityId,
        ["idempotencyGuid"] = IdempotencyGuid(request.InvoiceId).ToString(),
        ["routing"] = new JsonObject
        {
            ["eIdentifiers"] = new JsonArray(new JsonObject { ["scheme"] = scheme, ["id"] = request.Receiver.Identifier }),
            ["emails"] = new JsonArray(),
        },
        ["document"] = new JsonObject
        {
            ["documentType"] = "invoice",
            ["rawDocumentData"] = new JsonObject
            {
                ["document"] = Convert.ToBase64String(request.Ubl),
                ["parse"] = true,
                ["parseStrategy"] = "ubl",
                ["processId"] = UblWriter.ProfileId,
                ["documentTypeId"] = request.DocumentTypeId,
            },
        },
    };

    /// <summary>A stable GUID per document (first 16 bytes of SHA-256 of the id): retries reuse it.</summary>
    internal static Guid IdempotencyGuid(string invoiceId) => new(SHA256.HashData(Encoding.UTF8.GetBytes("storecove:" + invoiceId))[..16]);

    private static string Errors(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Array)
            return string.Join("; ", json.EnumerateArray().Select(e => e.TryGetProperty("details", out var d) ? d.ToString() : e.ToString()).Take(5));
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("errors", out var errors))
            return Errors(errors);
        return json.ValueKind == JsonValueKind.Undefined ? "no body" : json.ToString();
    }

    private async Task<(HttpStatusCode Status, JsonElement Json)> SendAsync(HttpMethod method, string relative, JsonObject body, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (string.IsNullOrEmpty(settings.ApiKey))
            throw new PeppolException(ProviderName, "Spark:Commerce:Invoicing:Peppol:Storecove:ApiKey is not set.");
        var baseUri = new Uri(settings.ApiBaseUrl.EndsWith('/') ? settings.ApiBaseUrl : settings.ApiBaseUrl + "/");
        if (baseUri.Scheme != Uri.UriSchemeHttps)
            throw new PeppolException(ProviderName, "refused to send the API key over a non-HTTPS base URL.");
        using var request = new HttpRequestMessage(method, new Uri(baseUri, relative))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
            return (response.StatusCode, default);
        try
        {
            using var document = JsonDocument.Parse(text);
            return (response.StatusCode, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return (response.StatusCode, default);
        }
    }
}
