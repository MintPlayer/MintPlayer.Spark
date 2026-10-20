using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Luxembourg;

/// <summary>Credit lines (PRD §4.4, §4.12).</summary>
internal static class LuxembourgLicences
{
    public static readonly SourceLicence Caclr = new(
        "ACT Registre national des localités et des rues (CACLR)", "CC0 1.0", "https://creativecommons.org/publicdomain/zero/1.0/",
        "Registre national des localités et des rues (CACLR), Administration du cadastre et de la topographie, via data.public.lu, CC0; credited although CC0 does not require it.",
        Modified: false);
}

/// <summary>
/// CACLR (<c>lu-caclr</c>): the national register of localities and streets, published weekly as a ZIP of fixed-width
/// ISO-8859-1 files (cantons, communes, localities, streets, postal codes, buildings). The download path holds a
/// <b>dated folder</b> (<c>…/20261019-023002/caclr.zip</c>), so the file is discovered through the data.public.lu
/// dataset API, never hard-coded; the change check is the MD5 checksum the API publishes for the resource.
/// </summary>
internal sealed partial class LuxembourgCaclrSource : RefFileSource
{
    public const string SourceId = "lu-caclr";
    public const string DatasetApi = "https://data.public.lu/api/1/datasets/registre-national-des-localites-et-des-rues/";

    public override string Id => SourceId;
    public override string Country => "lu";
    public override SourceLicence Licence => LuxembourgLicences.Caclr;
    public override IReadOnlyList<string> DownloadHosts => ["data.public.lu", "download.data.public.lu"];
    public override string DefaultSchedule => "0 0 5 * * 2";
    protected override string DownloadUrl => DatasetApi;
    protected override long MaxBytes => 512L * 1024 * 1024;

    /// <summary>The files the mapper reads (the inner names carry no extension; fixtures add <c>.cut</c>).</summary>
    protected override Regex? EntryPattern => CaclrFile();

    protected override IReadOnlyList<string> FixtureNames => ["CANTON.cut", "COMMUALL.cut", "LOCALITE.cut", "RUE.cut", "CODEPT.cut", "IMMEUBLE.cut"];

    [GeneratedRegex(@"^(CANTON|COMMUALL|LOCALITE|RUE|CODEPT|IMMEUBLE)(\.cut)?$")]
    internal static partial Regex CaclrFile();

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var dataset = await http.GetFromJsonAsync<Dataset>(DatasetApi, cancellationToken)
            ?? throw new InvalidDataException($"{SourceId}: the dataset API answered nothing.");
        var resource = dataset.Resources.FirstOrDefault(r => string.Equals(r.Title, "caclr.zip", StringComparison.OrdinalIgnoreCase)
                           || r.Url.EndsWith("/caclr.zip", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{SourceId}: the dataset lists no caclr.zip resource.");
        // The published checksum is the change check (RD1 step 2); the dated folder is the fallback version.
        var version = resource.Checksum?.Value is { Length: > 0 } sum
            ? $"{resource.Checksum.Type ?? "md5"}:{sum}"
            : Regex.Match(resource.Url, @"/(\d{8}-\d{6})/").Groups[1].Value;
        if (version.Length == 0)
            throw new InvalidDataException($"{SourceId}: {resource.Url} carries neither a checksum nor a dated folder.");
        if (!Uri.TryCreate(resource.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"{SourceId}: '{resource.Url}' is not an https URL.");
        return last?.Version == version ? null : new SourceVersion(version, uri.ToString());
    }

    private sealed class Dataset
    {
        [JsonPropertyName("resources")] public List<Resource> Resources { get; set; } = [];
    }

    private sealed class Resource
    {
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("checksum")] public Checksum? Checksum { get; set; }
    }

    private sealed class Checksum
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("value")] public string? Value { get; set; }
    }
}
