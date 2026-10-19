using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Invoicing.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Invoicing.Vat;

/// <summary>A VAT id split into its VIES country prefix and number.</summary>
public readonly record struct VatId(string CountryCode, string Number)
{
    public override string ToString() => CountryCode + Number;
}

/// <summary>The result of a VAT id check.</summary>
/// <param name="Valid">VIES says the number is valid now.</param>
/// <param name="Checked">The check reached VIES (false: unavailable, treat as unverified).</param>
/// <param name="RequestIdentifier">The consultation number (proof of the check), when VIES gave one.</param>
public sealed record VatIdCheckResult(bool Valid, bool Checked, string? Name = null, string? Address = null, string? RequestIdentifier = null, string? Error = null);

/// <summary>Checks B2B VAT ids (§4.8). The default asks VIES; a host may replace it.</summary>
public interface IVatIdValidator
{
    Task<VatIdCheckResult> CheckAsync(VatId vatId, CancellationToken cancellationToken);
}

/// <summary>VAT id syntax per member state (the shapes VIES documents in its FAQ); a syntax failure never reaches VIES.</summary>
public static partial class VatIdFormat
{
    private static readonly Dictionary<string, Regex> Shapes = new(StringComparer.Ordinal)
    {
        ["AT"] = new(@"^U\d{8}$"),
        ["BE"] = new(@"^[01]\d{9}$"),
        ["BG"] = new(@"^\d{9,10}$"),
        ["CY"] = new(@"^\d{8}[A-Z]$"),
        ["CZ"] = new(@"^\d{8,10}$"),
        ["DE"] = new(@"^\d{9}$"),
        ["DK"] = new(@"^\d{8}$"),
        ["EE"] = new(@"^\d{9}$"),
        ["EL"] = new(@"^\d{9}$"),
        ["ES"] = new(@"^[A-Z0-9]\d{7}[A-Z0-9]$"),
        ["FI"] = new(@"^\d{8}$"),
        ["FR"] = new(@"^[A-HJ-NP-Z0-9]{2}\d{9}$"),
        ["HR"] = new(@"^\d{11}$"),
        ["HU"] = new(@"^\d{8}$"),
        ["IE"] = new(@"^(\d{7}[A-W][A-I]?|\d[A-Z+*]\d{5}[A-W])$"),
        ["IT"] = new(@"^\d{11}$"),
        ["LT"] = new(@"^(\d{9}|\d{12})$"),
        ["LU"] = new(@"^\d{8}$"),
        ["LV"] = new(@"^\d{11}$"),
        ["MT"] = new(@"^\d{8}$"),
        ["NL"] = new(@"^\d{9}B\d{2}$"),
        ["PL"] = new(@"^\d{10}$"),
        ["PT"] = new(@"^\d{9}$"),
        ["RO"] = new(@"^\d{2,10}$"),
        ["SE"] = new(@"^\d{12}$"),
        ["SI"] = new(@"^\d{8}$"),
        ["SK"] = new(@"^\d{10}$"),
        ["XI"] = new(@"^(\d{9}|\d{12}|GD\d{3}|HA\d{3})$"),
    };

    /// <summary>Parses <c>BE 0123.456.789</c>-like input (spaces, dots and dashes dropped, upper-cased).</summary>
    public static VatId? Parse(string? input)
    {
        if (input is null)
            return null;
        var compact = Separators().Replace(input, "").ToUpperInvariant();
        if (compact.Length < 4)
            return null;
        var prefix = compact[..2];
        if (prefix == "GR")
            prefix = "EL";
        var number = compact[2..];
        return Shapes.TryGetValue(prefix, out var shape) && shape.IsMatch(number) ? new VatId(prefix, number) : null;
    }

    /// <summary>The ISO country of a VIES prefix (<c>EL</c> → <c>GR</c>).</summary>
    public static string IsoCountry(VatId id) => id.CountryCode == "EL" ? "GR" : id.CountryCode;

    [GeneratedRegex(@"[\s.\-]")]
    private static partial Regex Separators();
}

/// <summary>
/// VIES over its REST API (<c>POST /check-vat-number</c>, documented at
/// <see href="https://ec.europa.eu/taxation_customs/vies/#/technical-information"/>), cached in
/// <see cref="InvoiceVatCheck"/> for <see cref="ViesOptions.CacheFor"/>. An unavailable VIES (a member-state service down,
/// <c>MS_UNAVAILABLE</c>) is <b>not</b> a valid answer: the id counts as unverified and VAT is charged.
/// </summary>
internal sealed class ViesVatIdValidator(
    IHttpClientFactory httpClients,
    IOptionsMonitor<SparkCommerceInvoicingOptions> options,
    IDocumentStore store,
    TimeProvider time,
    ILogger<ViesVatIdValidator> logger) : IVatIdValidator
{
    public const string HttpClientName = "spark-commerce-invoicing-vies";

    public async Task<VatIdCheckResult> CheckAsync(VatId vatId, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue.Vies;
        if (!settings.Enabled)
            return new VatIdCheckResult(false, false, Error: "VIES checks are off.");

        var id = InvoiceVatCheck.IdFor(vatId.CountryCode, vatId.Number);
        using (var session = store.OpenAsyncSession())
        {
            var cached = await session.LoadAsync<InvoiceVatCheck>(id, cancellationToken);
            if (cached is { Valid: true } && time.GetUtcNow() - cached.CheckedAt < settings.CacheFor)
                return new VatIdCheckResult(true, true, cached.Name, cached.Address, cached.RequestIdentifier);
        }

        VatIdCheckResult result;
        try
        {
            var client = httpClients.CreateClient(HttpClientName);
            client.Timeout = settings.Timeout;
            var requester = VatIdFormat.Parse(settings.RequesterVatId);
            var body = new ViesRequest(vatId.CountryCode, vatId.Number, requester?.CountryCode, requester?.Number);
            using var response = await client.PostAsJsonAsync(settings.BaseUrl.TrimEnd('/') + "/check-vat-number", body, ViesJson.Options, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            result = ViesResponseParser.Parse(text, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("VIES could not be reached for a {Country} VAT id: {Error}", vatId.CountryCode, ex.Message);
            result = new VatIdCheckResult(false, false, Error: "VIES unavailable");
        }

        if (result.Checked)
        {
            using var session = store.OpenAsyncSession();
            await session.StoreAsync(new InvoiceVatCheck
            {
                Id = id, CountryCode = vatId.CountryCode, Number = vatId.Number, Valid = result.Valid, Name = result.Name,
                Address = result.Address, RequestIdentifier = result.RequestIdentifier, CheckedAt = time.GetUtcNow(),
            }, id, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
        return result;
    }

    private sealed record ViesRequest(
        [property: JsonPropertyName("countryCode")] string CountryCode,
        [property: JsonPropertyName("vatNumber")] string VatNumber,
        [property: JsonPropertyName("requesterMemberStateCode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequesterMemberStateCode,
        [property: JsonPropertyName("requesterNumber"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequesterNumber);
}

internal static class ViesJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>Reads VIES' <c>check-vat-number</c> answer (fields <c>valid</c>, <c>name</c>, <c>address</c>,
/// <c>requestIdentifier</c>; an error answer carries <c>errorWrappers[].error</c>, e.g. <c>MS_UNAVAILABLE</c>).</summary>
internal static class ViesResponseParser
{
    public static VatIdCheckResult Parse(string text, int status)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new VatIdCheckResult(false, false, Error: $"VIES answered {status} with no body.");
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.TryGetProperty("errorWrappers", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var error = errors[0].TryGetProperty("error", out var e) ? e.GetString() : "error";
            // INVALID_INPUT is an answer (the number is malformed); everything else (MS_UNAVAILABLE, TIMEOUT, ...) is not.
            return new VatIdCheckResult(false, error == "INVALID_INPUT", Error: error);
        }
        if (status is < 200 or > 299 || !root.TryGetProperty("valid", out var valid))
            return new VatIdCheckResult(false, false, Error: $"VIES answered {status}.");
        static string? Text(JsonElement r, string name)
            => r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s && s != "---" ? s : null;
        return new VatIdCheckResult(valid.ValueKind == JsonValueKind.True, true, Text(root, "name"), Text(root, "address"), Text(root, "requestIdentifier"));
    }
}
