using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Commerce.Invoicing.EInvoicing;

namespace MintPlayer.Spark.Commerce.Invoicing.Peppol.Billit;

/// <summary>
/// <c>Spark:Commerce:Invoicing:Peppol:Billit</c>. The API key comes from configuration or user-secrets only, is read per
/// call and never logged.
/// </summary>
public sealed class BillitOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Invoicing:Peppol:Billit";

    /// <summary>The API key (header <c>ApiKey</c>).</summary>
    public string? ApiKey { get; set; }

    /// <summary>The Billit party that sends for the platform (header <c>PartyID</c>).</summary>
    public string? PartyId { get; set; }

    /// <summary>Parties per issuer key (a seller's organization id) in connected mode.</summary>
    public Dictionary<string, string> Parties { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Production <c>https://api.billit.be/</c>; sandbox <c>https://api.sandbox.billit.be/</c>.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.billit.be/";
}

/// <summary>
/// Billit (<see href="https://docs.billit.be/docs/send-ubl-to-peppol-1"/>): <c>POST v1/peppol/sendxml</c> with the UBL as
/// the <c>XML</c> string of a JSON body, the party and key in the headers; a 200 answers the inbox item id. Billit runs
/// the Peppol validation rules on submission, so a refused document comes back with its rule messages. The receiver
/// check uses <c>GET v1/peppol/participantInformation/{id}</c>.
/// </summary>
/// <remarks>
/// ⚠️ Doc-derived (round 4, D37). Confirmed from Billit's public page (2026-10-19): the method, path, hosts and the
/// <c>XML</c> body field. <b>Not</b> confirmed: the header names <c>ApiKey</c>/<c>PartyID</c> (the page points to its
/// authentication page) and the participant-information endpoint and its answer; check both with a sandbox key.
/// </remarks>
internal sealed class BillitAccessPoint(IHttpClientFactory httpClients, IOptionsMonitor<BillitOptions> options) : IPeppolAccessPoint
{
    public const string ProviderName = "billit";
    public const string HttpClientName = "spark-commerce-invoicing-billit";

    public string Name => ProviderName;

    public async Task<PeppolLookupResult> LookupAsync(PeppolParticipant receiver, string documentTypeId, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, "v1/peppol/participantInformation/" + Uri.EscapeDataString(receiver.Identifier), null, null);
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new PeppolLookupResult(false, "Billit: the receiver is not registered on Peppol.");
        if (!response.IsSuccessStatusCode)
            throw new PeppolException(ProviderName, $"participant lookup answered {(int)response.StatusCode}.", (int)response.StatusCode);
        return new PeppolLookupResult(ParticipantRegistered(text));
    }

    /// <summary>Reads the lookup answer: a <c>Registered</c> flag when present, else any participant record counts.</summary>
    internal static bool ParticipantRegistered(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (string.Equals(property.Name, "Registered", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        return property.Value.GetBoolean();
                }
                return true;
            }
            return root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<PeppolSendResult> SendAsync(PeppolSendRequest request, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var party = settings.Parties.TryGetValue(request.IssuerKey, out var own) ? own : settings.PartyId;
        if (party is not { Length: > 0 })
            return new PeppolSendResult(false, null, $"No Billit party for issuer '{request.IssuerKey}'.");
        var body = new JsonObject { ["XML"] = Encoding.UTF8.GetString(request.Ubl) };
        using var message = Request(HttpMethod.Post, "v1/peppol/sendxml", party, body);
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(message, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
            return new PeppolSendResult(true, InboxItemId(text));
        var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
        return new PeppolSendResult(false, null, $"Billit answered {(int)response.StatusCode}: {Truncate(text, 500)}", retryable);
    }

    /// <summary>The inbox item id from the 200 answer: a JSON object's <c>InboxItemID</c>, or the bare value.</summary>
    internal static string? InboxItemId(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (string.Equals(property.Name, "InboxItemID", StringComparison.OrdinalIgnoreCase))
                        return property.Value.ToString();
                }
                return null;
            }
            return root.ToString();
        }
        catch (JsonException)
        {
            return text.Trim();
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string relative, string? party, JsonObject? body)
    {
        var settings = options.CurrentValue;
        if (string.IsNullOrEmpty(settings.ApiKey))
            throw new PeppolException(ProviderName, "Spark:Commerce:Invoicing:Peppol:Billit:ApiKey is not set.");
        var baseUri = new Uri(settings.ApiBaseUrl.EndsWith('/') ? settings.ApiBaseUrl : settings.ApiBaseUrl + "/");
        if (baseUri.Scheme != Uri.UriSchemeHttps)
            throw new PeppolException(ProviderName, "refused to send the API key over a non-HTTPS base URL.");
        var request = new HttpRequestMessage(method, new Uri(baseUri, relative));
        request.Headers.TryAddWithoutValidation("ApiKey", settings.ApiKey);
        if ((party ?? settings.PartyId) is { Length: > 0 } p)
            request.Headers.TryAddWithoutValidation("PartyID", p);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>Registration of the Billit access point.</summary>
public static class BillitPeppolExtensions
{
    /// <summary>Adds Billit (<c>billit</c>) as Peppol access point. Credentials: <c>Spark:Commerce:Invoicing:Peppol:Billit</c> (user-secrets).</summary>
    public static SparkInvoicingBuilder UseBillitPeppol(this SparkInvoicingBuilder invoicing, Action<BillitOptions>? configure = null)
    {
        var services = invoicing.Spark.Services;
        services.AddSparkConfigurationSection<BillitOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.AddHttpClient(BillitAccessPoint.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        return invoicing.Spark.AddPeppolAccessPoint<BillitAccessPoint>();
    }
}
