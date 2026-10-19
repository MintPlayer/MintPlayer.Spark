using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using MintPlayer.Spark.ReferenceData.Build;

namespace MintPlayer.Spark.ReferenceData.Sources.Tedb;

/// <summary>
/// The Commission's TEDB VAT retrieval service (<c>eu-tedb</c>, commerce GC10): a documented SOAP operation,
/// <c>retrieveVatRates</c> (WSDL https://ec.europa.eu/taxation_customs/tedb/ws/VatRetrievalService.wsdl), asked for
/// the rates in force on one date for the configured member states. No checksum is published, so the change check
/// hashes the response (RD1). The fixture is a recorded response of 2026-10-01 for BE, DE, FR, LU and NL (CN codes
/// dropped for size). ⚠️ Licence: the Commission's reuse notice (Decision 2011/833/EU) is assumed; verify in RS23.
/// </summary>
internal sealed class TedbSource : IReferenceDataSource
{
    public const string SourceId = "eu-tedb";
    public const string Endpoint = "https://ec.europa.eu/taxation_customs/tedb/ws/";
    public static readonly string[] MemberStates = ["BE", "DE", "FR", "LU", "NL"];

    public string Id => SourceId;
    public string Country => "*";
    public SourceLicence Licence { get; } = new(
        "European Commission TEDB", "Commission reuse notice (Decision 2011/833/EU)", "https://commission.europa.eu/legal-notice_en",
        "Source: European Commission, Taxes in Europe Database (TEDB). VAT rates are suggestions; the host sets its own.");
    public IReadOnlyList<string> DownloadHosts => ["ec.europa.eu"];
    public string DefaultSchedule => "0 0 5 * * 1";

    public async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var bytes = await RequestAsync(http, DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime), cancellationToken);
        var sha = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        return last?.Sha256 == sha ? null : new SourceVersion(sha[7..19], Endpoint, Sha256: sha);
    }

    public async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(workDirectory.FullName, "retrieveVatRates.xml");
        var bytes = await RequestAsync(http, DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime), cancellationToken);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return new SourceInput { SourceId = Id, Version = version.Version, Url = Endpoint, Files = [new SourceFile("retrieveVatRates.xml", () => File.OpenRead(path))] };
    }

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Files = EmbeddedSourceFiles.Under(typeof(TedbSource).Assembly, "Fixtures/"),
    };

    private static async Task<byte[]> RequestAsync(HttpClient http, DateOnly situationOn, CancellationToken cancellationToken)
    {
        var body = new StringBuilder()
            .Append("<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" ")
            .Append("xmlns:urn=\"urn:ec.europa.eu:taxud:tedb:services:v1:IVatRetrievalService\" ")
            .Append("xmlns:urn1=\"urn:ec.europa.eu:taxud:tedb:services:v1:IVatRetrievalService:types\">")
            .Append("<soapenv:Header/><soapenv:Body><urn:retrieveVatRatesReqMsg><urn1:memberStates>");
        foreach (var state in MemberStates)
            body.Append("<urn1:isoCode>").Append(state).Append("</urn1:isoCode>");
        body.Append("</urn1:memberStates><urn1:situationOn>").Append(situationOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append("</urn1:situationOn></urn:retrieveVatRatesReqMsg></soapenv:Body></soapenv:Envelope>");
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/xml") };
        request.Headers.Add("SOAPAction", "urn:ec.europa.eu:taxud:tedb:services:v1:VatRetrievalService/RetrieveVatRates");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}

/// <summary>Maps a <c>retrieveVatRatesRespMsg</c> into <see cref="VatRateSuggestion"/> documents.</summary>
internal sealed partial class TedbMapper : IReferenceDataMapper
{
    [GeneratedRegex(@"\.xml$")]
    private static partial Regex XmlFile();

    public IReadOnlyList<string> Countries => [.. TedbSource.MemberStates.Select(s => s.ToLowerInvariant())];
    public IReadOnlyList<string> SourceIds => [TedbSource.SourceId];
    public int Order => 20;
    public IEnumerable<LedgerEntry> ShippedLedger() => [];

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        if (!inputs.TryGetValue(TedbSource.SourceId, out var input) || input.Find(XmlFile()) is not { } file)
            return Task.CompletedTask;
        foreach (var rate in Read(file.Open()))
        {
            var category = rate.Category ?? "standard";
            var id = RefIds.VatRate(rate.MemberState, rate.Type, Slug(category), rate.SituationOn);
            if (build.VatRates.ContainsKey(id))
                continue; // TEDB repeats a result once per CN-code group; the first is kept.
            build.VatRates[id] = new VatRateSuggestion
            {
                Id = id,
                Country = rate.MemberState.ToLowerInvariant(),
                Type = rate.Type,
                RateType = rate.RateType,
                RateBasisPoints = (int)Math.Round(rate.Value * 100m, MidpointRounding.AwayFromZero),
                Category = category,
                CategoryDescription = rate.CategoryDescription,
                SituationOn = rate.SituationOn,
                Comment = rate.Comment,
                Source = [TedbSource.SourceId],
            };
        }
        return Task.CompletedTask;
    }

    internal sealed record TedbRate(string MemberState, string Type, string RateType, decimal Value, DateOnly SituationOn, string? Category, string? CategoryDescription, string? Comment);

    /// <summary>Streams the response's <c>vatRateResults</c> (DTD prohibited, no resolver).</summary>
    internal static IEnumerable<TedbRate> Read(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true });
        while (reader.ReadToFollowing("vatRateResults", "urn:ec.europa.eu:taxud:tedb:services:v1:IVatRetrievalService:types"))
        {
            using var result = reader.ReadSubtree();
            string? state = null, type = null, rateType = null, category = null, description = null, comment = null;
            decimal value = 0;
            DateOnly situation = default;
            var path = new Stack<string>();
            // ReadElementContentAsString leaves the reader on the next node, so the loop advances only when it did not read.
            var advance = true;
            while (!advance || result.Read())
            {
                advance = true;
                if (result.NodeType == XmlNodeType.Element)
                {
                    var name = result.LocalName;
                    var parent = path.Count > 0 ? path.Peek() : string.Empty;
                    if (result.IsEmptyElement) continue;
                    string? text = (parent, name) switch
                    {
                        ("vatRateResults", "memberState" or "type" or "situationOn" or "comment") or ("rate", "type" or "value") or ("category", "identifier" or "description")
                            => result.ReadElementContentAsString(),
                        _ => null,
                    };
                    if (text is null)
                    {
                        path.Push(name);
                        continue;
                    }
                    advance = false;
                    switch ((parent, name))
                    {
                        case ("vatRateResults", "memberState"): state = text; break;
                        case ("vatRateResults", "type"): type = text; break;
                        case ("rate", "type"): rateType = text; break;
                        case ("rate", "value"): value = decimal.Parse(text, CultureInfo.InvariantCulture); break;
                        case ("vatRateResults", "situationOn"): situation = DateOnly.ParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
                        case ("category", "identifier"): category = text; break;
                        case ("category", "description"): description = text; break;
                        case ("vatRateResults", "comment"): comment = text; break;
                    }
                }
                else if (result.NodeType == XmlNodeType.EndElement && path.Count > 0)
                    path.Pop();
            }
            if (state is not null && type is not null)
                yield return new TedbRate(state, type, rateType ?? string.Empty, value, situation, category, description, comment);
        }
    }

    private static string Slug(string category)
    {
        var slug = new StringBuilder();
        foreach (var c in category.ToLowerInvariant())
            slug.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return slug.ToString().Trim('-') is { Length: > 0 } s ? (s.Length > 60 ? s[..60] : s) : "category";
    }
}

public static class ReferenceDataTedbExtensions
{
    /// <summary>EU VAT-rate suggestions from TEDB (commerce GC10). Off by default: a source runs on a schedule only when enabled.</summary>
    public static ReferenceDataBuilder AddTedbVatRates(this ReferenceDataBuilder builder)
        => builder.AddSource<TedbSource>().AddMapper<TedbMapper>();
}
