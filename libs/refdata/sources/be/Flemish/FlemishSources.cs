using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MintPlayer.Spark.ReferenceData.Sources.Belgium.Readers;

namespace MintPlayer.Spark.ReferenceData.Sources.Belgium.Flemish;

/// <summary>
/// The Flemish 2025 merger diff files (<c>be-flemish-diff</c>, Digitaal Vlaanderen, a one-off publication of
/// 2024-12-20; PRD §4.4): old → new NIS codes of the 28 merging municipalities and old → new ids of 7,018 street names
/// (359 renamed to resolve homonyms). Pinned by SHA-256: a different file is refused, never imported silently. Only the
/// municipality and street files are read; the address file (≈79 MB) is never extracted (RD5), nor the Rijksregister
/// variant of the street file.
/// </summary>
internal sealed partial class FlemishDiffSource : RefFileSource
{
    public const string SourceId = "be-flemish-diff";
    public const string FileUrl = "https://assets.vlaanderen.be/raw/upload/v1734692529/repositories-prd/Verschilbestanden_Fusies_20-12-2024_wmyxyo.zip";

    /// <summary>SHA-256 of the zip as downloaded on 2026-10-19 (4,248,291 bytes).</summary>
    public const string PinnedSha256 = "sha256:1e0f90bb14ed7260305203711027591470065acfa1c4ad2a2a8119ae2b7f6bf8";

    public const string Version = "20241220";

    [GeneratedRegex(@"^fusies-verschilbestand-(gemeenten|straatnamen)-\d{8}(\.cut)?\.csv$")]
    internal static partial Regex Files();

    [GeneratedRegex(@"^fusies-verschilbestand-gemeenten-\d{8}(\.cut)?\.csv$")]
    internal static partial Regex MunicipalityFile();

    [GeneratedRegex(@"^fusies-verschilbestand-straatnamen-\d{8}(\.cut)?\.csv$")]
    internal static partial Regex StreetFile();

    public override string Id => SourceId;
    public override string Country => "be";
    public override SourceLicence Licence => BelgiumLicences.Flemish;
    public override IReadOnlyList<string> DownloadHosts => ["assets.vlaanderen.be"];
    public override string DefaultSchedule => "0 0 5 1 1 *";
    protected override string DownloadUrl => FileUrl;
    protected override Regex? EntryPattern => Files();
    protected override long MaxBytes => 256L * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames =>
        ["fusies-verschilbestand-gemeenten-20241213.cut.csv", "fusies-verschilbestand-straatnamen-20241218.cut.csv"];

    /// <summary>A one-off file: imported once; "no change" for ever after.</summary>
    public override Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
        => Task.FromResult(last?.Version == Version ? null : new SourceVersion(Version, FileUrl, Sha256: PinnedSha256));

    public override async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        // The hash is checked before the archive is opened: an unreviewed file is never read.
        var zip = Path.Combine(workDirectory.FullName, "verschilbestanden.zip");
        var sha = await RefDownloads.DownloadAsync(http, FileUrl, zip, MaxBytes, cancellationToken);
        if (!string.Equals(sha, PinnedSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"{SourceId}: the download's SHA-256 ({sha}) is not the pinned one; refusing an unreviewed file.");
        return new SourceInput { SourceId = Id, Version = version.Version, Url = FileUrl, Sha256 = sha, Files = RefZip.Extract(zip, Files(), workDirectory, MaxBytes) };
    }
}

/// <summary>One row of the municipality diff file: an old NIS code and the code it continues in from 2025-01-01.</summary>
public sealed record FlemishMunicipalityMapping(string OldNis, string OldName, string NewNis, string NewName);

/// <summary>One row of the street diff file: an old street-name id and the id (and name) it continues in.</summary>
public sealed record FlemishStreetMapping(
    string OldNis, string OldNamespace, string OldId, string OldName, string OldHomonym,
    string NewNis, string NewNamespace, string NewId, string NewName, string NewHomonym);

/// <summary>
/// The diff files: <c>;</c>-separated, CRLF, unquoted; the municipality file starts with a BOM, the street file does
/// not. Ids are register URIs (<c>https://data.vlaanderen.be/id/straatnaam/3679</c>); the namespace is the URI
/// without its last segment.
/// </summary>
public static class FlemishDiffReader
{
    public static IEnumerable<FlemishMunicipalityMapping> Municipalities(Stream stream)
    {
        foreach (var row in RefCsv.ReadRecords(stream, ';', quotes: false))
            yield return new FlemishMunicipalityMapping(row["OUD NIS code"], row["OUD gemeentenaam"], row["NIEUW NIS code"], row["NIEUW gemeentenaam"]);
    }

    public static IEnumerable<FlemishStreetMapping> Streets(Stream stream)
    {
        foreach (var row in RefCsv.ReadRecords(stream, ';', quotes: false))
        {
            var (oldNs, oldId) = SplitUri(row["OUD straatnaamid"]);
            var (newNs, newId) = SplitUri(row["NIEUW straatnaamid"]);
            if (oldId.Length == 0 || newId.Length == 0)
                throw new InvalidDataException($"Flemish street diff: a row without ids ({string.Join(';', row.Fields)}).");
            yield return new FlemishStreetMapping(
                row["OUD NIS code"], oldNs, oldId, row["OUD straatnaam"], row["OUD homoniemtoevoeging"],
                row["NIEUW NIS code"], newNs, newId, row["NIEUW straatnaam"], row["NIEUW homoniemtoevoeging"]);
        }
    }

    private static (string Namespace, string Id) SplitUri(string uri)
    {
        var trimmed = uri.Trim().TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? (string.Empty, trimmed) : (trimmed[..slash], trimmed[(slash + 1)..]);
    }
}

/// <summary>
/// The Flemish Basisregisters API v2 (<c>be-flemish-api</c>, public, no key; PRD §4.4): every municipality's
/// <c>officieleTalen</c>/<c>faciliteitenTalen</c> and status (285 in use + 42 <c>gehistoreerd</c>), and the retired
/// street names of retired municipalities. The change check hashes the municipality list (no ETag is published; RD1).
/// Each resource is saved as its own response (<c>flemish-api-gemeente-{nis}.json</c>, <c>flemish-api-straatnaam-{id}.json</c>).
/// Change feeds need an API key and are not used.
/// </summary>
internal sealed partial class FlemishApiSource : IReferenceDataSource
{
    public const string SourceId = "be-flemish-api";
    private const string Base = "https://api.basisregisters.vlaanderen.be/v2/";
    private const int MaxPages = 200;

    [GeneratedRegex(@"^flemish-api-(gemeente|straatnaam)-\d{1,9}\.json$")]
    internal static partial Regex Files();

    public string Id => SourceId;
    public string Country => "be";
    public SourceLicence Licence => BelgiumLicences.Flemish;
    public IReadOnlyList<string> DownloadHosts => ["api.basisregisters.vlaanderen.be"];
    public string DefaultSchedule => "0 0 5 * * 1";

    public async Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
    {
        var list = await ListAsync(http, "gemeenten?limit=500", "gemeenten", cancellationToken);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', list.Select(e => e.GetRawText())))));
        return last?.Sha256 == digest ? null : new SourceVersion(digest[7..19], Base + "gemeenten", Sha256: digest);
    }

    public async Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var files = new List<SourceFile>();
        foreach (var municipality in await ListAsync(http, "gemeenten?limit=500", "gemeenten", cancellationToken))
        {
            var nis = ObjectId(municipality);
            files.Add(await SaveAsync(http, $"gemeenten/{nis}", $"flemish-api-gemeente-{nis}.json", workDirectory, cancellationToken));
            if (municipality.TryGetProperty("gemeenteStatus", out var status) && status.GetString() == "gehistoreerd")
                foreach (var street in await ListAsync(http, $"straatnamen?niscode={nis}&status=gehistoreerd&limit=500", "straatnamen", cancellationToken))
                {
                    var id = ObjectId(street);
                    files.Add(await SaveAsync(http, $"straatnamen/{id}", $"flemish-api-straatnaam-{id}.json", workDirectory, cancellationToken));
                }
        }
        return new SourceInput { SourceId = Id, Version = version.Version, Url = Base, Sha256 = version.Sha256, Files = files };
    }

    public SourceInput Fixture() => new()
    {
        SourceId = Id,
        Version = "fixture",
        Files = [.. EmbeddedSourceFiles.Under(typeof(FlemishApiSource).Assembly, "Fixtures/").Where(f => Files().IsMatch(f.Name))],
    };

    private static string ObjectId(JsonElement item)
    {
        var id = item.GetProperty("identificator").GetProperty("objectId").GetString() ?? string.Empty;
        return id.Length is > 0 and <= 9 && id.All(char.IsAsciiDigit) ? id : throw new InvalidDataException($"{SourceId}: unexpected objectId '{id}'.");
    }

    private static async Task<SourceFile> SaveAsync(HttpClient http, string resource, string name, DirectoryInfo workDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(workDirectory.FullName, name);
        await RefDownloads.DownloadAsync(http, Base + resource, path, 4L * 1024 * 1024, cancellationToken);
        return new SourceFile(name, () => File.OpenRead(path));
    }

    /// <summary>Every item of a paged list, following <c>volgende</c> (the next page) on the same host only.</summary>
    private static async Task<List<JsonElement>> ListAsync(HttpClient http, string first, string property, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        var url = Base + first;
        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            using var document = JsonDocument.Parse(await http.GetStringAsync(url, cancellationToken));
            if (document.RootElement.TryGetProperty(property, out var array))
                items.AddRange(array.EnumerateArray().Select(e => e.Clone()));
            url = document.RootElement.TryGetProperty("volgende", out var next) && next.GetString() is { } n && n.StartsWith(Base, StringComparison.Ordinal) ? n : null;
        }
        return items;
    }
}

/// <summary>A municipality as the Flemish API describes it.</summary>
public sealed record FlemishApiMunicipality(string Nis, bool Retired, IReadOnlyList<string> OfficialLanguages, IReadOnlyList<string> FacilityLanguages, IReadOnlyList<(string Lang, string Value)> Names);

/// <summary>A street name as the Flemish API describes it.</summary>
public sealed record FlemishApiStreet(string Id, string Municipality, bool Retired, IReadOnlyList<(string Lang, string Value)> Names, IReadOnlyList<string> Homonyms);

/// <summary>Reads saved Flemish API v2 detail responses (<c>Gemeente</c>, <c>Straatnaam</c>). Unknown shapes are refused.</summary>
public static class FlemishApiReader
{
    public static object Read(Stream stream)
    {
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var type = root.TryGetProperty("@type", out var t) ? t.GetString() : null;
        var id = root.GetProperty("identificator").GetProperty("objectId").GetString() ?? string.Empty;
        return type switch
        {
            "Gemeente" => new FlemishApiMunicipality(
                id,
                Status(root, "gemeenteStatus") == "gehistoreerd",
                Strings(root, "officieleTalen"),
                Strings(root, "faciliteitenTalen"),
                Names(root, "gemeentenamen")),
            "Straatnaam" => new FlemishApiStreet(
                id,
                root.GetProperty("gemeente").GetProperty("objectId").GetString() ?? string.Empty,
                Status(root, "straatnaamStatus") == "gehistoreerd",
                Names(root, "straatnamen"),
                Homonyms(root)),
            _ => throw new InvalidDataException($"Flemish API: an unexpected resource type '{type}'."),
        };
    }

    /// <summary><c>homoniemToevoegingen</c>: an array of (spelling, taal) objects in the detail response.</summary>
    private static IReadOnlyList<string> Homonyms(JsonElement root)
    {
        var result = new List<string>();
        if (root.TryGetProperty("homoniemToevoegingen", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("spelling", out var s) && s.GetString() is { Length: > 0 } value)
                    result.Add(value);
                else if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                    result.Add(text);
        return result;
    }

    private static string? Status(JsonElement root, string name) => root.TryGetProperty(name, out var s) ? s.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement root, string name)
        => root.TryGetProperty(name, out var array) ? [.. array.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0)] : [];

    private static IReadOnlyList<(string, string)> Names(JsonElement root, string name)
        => root.TryGetProperty(name, out var array)
            ? [.. array.EnumerateArray().Select(x => (x.GetProperty("taal").GetString() ?? string.Empty, x.GetProperty("spelling").GetString() ?? string.Empty))]
            : [];
}
