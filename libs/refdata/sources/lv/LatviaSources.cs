using System.Text.Json;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Latvia;

/// <summary>Credit line (PRD §4.4, §4.12; research §4).</summary>
internal static class LatviaLicences
{
    /// <summary>
    /// data.gov.lv labels the dataset CC BY 4.0, Geolatvija CC0 1.0: the stricter CC BY is followed. VZD prescribes no
    /// credit text, so the credit names the publisher, the register and the licence, and states the modification.
    /// </summary>
    public static readonly SourceLicence Var = new(
        "VZD State Address Register", "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/",
        "Valsts zemes dienests (State Land Service of Latvia), Valsts adrešu reģistrs (https://data.gov.lv/dati/lv/dataset/varis-atvertie-dati), CC BY 4.0. " +
        "The data was modified: periods, hierarchy and lineage are derived from it, and postcodes are linked to streets without house numbers.");
}

/// <summary>
/// The VZD State Address Register open data (<c>lv-var</c>): daily CSVs on data.gov.lv, one per object type
/// (<c>aw_rajons</c>, <c>aw_novads</c>, <c>aw_pilseta</c>, <c>aw_pagasts</c>, <c>aw_ciems</c>, <c>aw_iela</c>,
/// <c>aw_eka</c>), each current table with a <c>*_his</c> twin holding the earlier versions.
/// <para>
/// <b>Change check:</b> the CKAN <c>package_show</c> of the dataset; the version is the latest <c>last_modified</c> of
/// the resources the mapper reads (data.gov.lv publishes no checksum file). <b>Fetch:</b> the resource URLs come from
/// the same response and are matched by file name (<c>aw_{table}.csv</c>), never hard-coded, because each resource sits
/// under its own UUID. The address file (<c>aw_eka</c>, ≈140 MB) is streamed for the street ↔ postcode links only;
/// the flat file (<c>aw_dziv</c>) is never fetched (RD5: no house numbers).
/// </para>
/// </summary>
internal sealed partial class LatviaAddressRegisterSource : RefFileSource
{
    public const string SourceId = "lv-var";
    public const string PackageId = "6b06a7e8-dedf-4705-a47b-2a7c51177473";
    public const string PackageUrl = "https://data.gov.lv/dati/api/3/action/package_show?id=" + PackageId;

    /// <summary>The tables the mapper reads; the address file only for its postcodes.</summary>
    public static readonly string[] Tables =
        ["aw_rajons", "aw_novads", "aw_novads_his", "aw_pilseta", "aw_pilseta_his", "aw_pagasts", "aw_pagasts_his",
         "aw_ciems", "aw_ciems_his", "aw_iela", "aw_iela_his", "aw_eka"];

    public override string Id => SourceId;
    public override string Country => "lv";
    public override SourceLicence Licence => LatviaLicences.Var;
    public override IReadOnlyList<string> DownloadHosts => ["data.gov.lv"];

    /// <summary>The register publishes daily (around 03:40 UTC).</summary>
    public override string DefaultSchedule => "0 30 5 * * *";

    protected override string DownloadUrl => PackageUrl;

    protected override IReadOnlyList<string> FixtureNames =>
    [
        "aw_rajons.cut.csv", "aw_novads.cut.csv", "aw_novads_his.cut.csv", "aw_pilseta.cut.csv", "aw_pilseta_his.cut.csv",
        "aw_pagasts.cut.csv", "aw_pagasts_his.cut.csv", "aw_iela.cut.csv", "aw_iela_his.cut.csv", "aw_eka.cut.csv",
    ];

    [GeneratedRegex(@"/(?<table>aw_[a-z_]+)\.csv$")]
    private static partial Regex ResourceFile();

    public override async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var resources = await ResourcesAsync(http, cancellationToken);
        var version = resources.Values.Select(r => r.Modified).DefaultIfEmpty(string.Empty).Max(StringComparer.Ordinal)!;
        if (version.Length == 0)
            throw new InvalidDataException($"{Id}: data.gov.lv lists none of the address-register tables.");
        return last?.Version == version ? null : new SourceVersion(version, PackageUrl);
    }

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var resources = await ResourcesAsync(http, cancellationToken);
        var files = new List<SourceFile>();
        foreach (var table in Tables)
        {
            if (!resources.TryGetValue(table, out var resource))
                throw new InvalidDataException($"{Id}: data.gov.lv no longer lists {table}.csv.");
            var path = Path.Combine(workDirectory.FullName, table + ".csv");
            await RefDownloads.DownloadAsync(http, resource.Url, path, MaxBytes, cancellationToken);
            files.Add(new SourceFile(table + ".csv", () => File.OpenRead(path)));
        }
        return new SourceInput { SourceId = Id, Version = version.Version, Url = PackageUrl, Files = files };
    }

    private static async Task<Dictionary<string, (string Url, string Modified)>> ResourcesAsync(HttpClient http, CancellationToken cancellationToken)
    {
        await using var stream = await http.GetStreamAsync(PackageUrl, cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var found = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var resource in document.RootElement.GetProperty("result").GetProperty("resources").EnumerateArray())
        {
            var url = resource.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            if (ResourceFile().Match(url) is not { Success: true } match || !Tables.Contains(match.Groups["table"].Value))
                continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "data.gov.lv")
                continue;
            var modified = resource.TryGetProperty("last_modified", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            found[match.Groups["table"].Value] = (url, modified);
        }
        return found;
    }
}
