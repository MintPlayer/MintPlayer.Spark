namespace MintPlayer.Spark.ReferenceData.Sources;

/// <summary>
/// One upstream source (PRD §4.4, RD1): <c>be-refnis</c>, <c>be-postalstreets</c>, <c>global-geonames</c>. The config
/// key is <see cref="Id"/>: <c>Spark:ReferenceData:Sources:{Id}:Schedule</c>.
/// <para>
/// A source only <em>finds</em> and <em>fetches</em> bytes: <see cref="CheckAsync"/> is the cheap change check that
/// runs first, <see cref="FetchAsync"/> downloads a changed version to a temporary file. Turning bytes into documents is
/// the country's <see cref="IReferenceDataMapper"/>. Downloads are data: parsed in-process by streaming readers, never
/// executed. Every source ships <see cref="Fixture"/>, a real sample cut from the real file, which CI and the basic
/// seed set (RD9) run through the same mapper.
/// </para>
/// </summary>
public interface IReferenceDataSource
{
    string Id { get; }

    /// <summary>Lower-case ISO 3166-1 alpha-2, or <c>*</c> for the global baseline.</summary>
    string Country { get; }

    SourceLicence Licence { get; }

    /// <summary>The hosts this source downloads from: the download client refuses every other host.</summary>
    IReadOnlyList<string> DownloadHosts { get; }

    /// <summary>The 6-field cron (UTC) used when <c>Spark:ReferenceData:Sources:{Id}:Schedule</c> is not set.</summary>
    string DefaultSchedule { get; }

    /// <summary>
    /// The change check (RD1 step 2), in order of preference: <c>ETag</c>/<c>Last-Modified</c>, a published checksum,
    /// a dated file name or version field, else the SHA-256 of the download. <see langword="null"/> = unchanged.
    /// </summary>
    Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken);

    /// <summary>Downloads <paramref name="version"/> into <paramref name="workDirectory"/> (RD1 step 4).</summary>
    Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken);

    /// <summary>The real sample shipped with the adapter (RS8): input for CI and for the basic seed set.</summary>
    SourceInput Fixture();
}

/// <summary>
/// One country's mapping (PRD §4.4): reads its sources' inputs, mints ids through the ledger, and adds documents and
/// lineage events to the build. Derived fields are computed afterwards by the engine, never here.
/// </summary>
public interface IReferenceDataMapper
{
    /// <summary>Lower-case ISO 3166-1 alpha-2 codes this mapper writes (the global baseline writes several).</summary>
    IReadOnlyList<string> Countries { get; }

    /// <summary>The source ids it reads; a missing input is skipped (the mapper maps what it is given).</summary>
    IReadOnlyList<string> SourceIds { get; }

    /// <summary>Mappers run in this order (the global baseline first, country adapters after it override its profiles).</summary>
    int Order => 0;

    /// <summary>The ledger lines this adapter ships (<c>Ledger/{cc}.ndjson</c>), so independent instances mint the same ids (Q-R5).</summary>
    IEnumerable<LedgerEntry> ShippedLedger();

    Task MapAsync(Build.RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken);
}

/// <summary>A source's licence and credit line (RD-DEF 4, §4.12).</summary>
/// <param name="Name">The publisher and dataset: "Statbel REFNIS".</param>
/// <param name="Licence">"CC BY 4.0", "CC0 1.0", "Unicode License v3".</param>
/// <param name="Attribution">The credit line as the publisher asks for it.</param>
/// <param name="Modified">Whether the data is changed on the way (CC BY asks to say so).</param>
public sealed record SourceLicence(string Name, string Licence, string? LicenceUrl, string Attribution, bool Modified = true)
{
    /// <summary>The compact form every API response carries: "Statbel (CC BY 4.0)".</summary>
    public string Compact => $"{Name} ({Licence})";
}

/// <summary>A version of a source the change check found.</summary>
public sealed record SourceVersion(string Version, string Url, string? ETag = null, DateTimeOffset? LastModified = null, string? Sha256 = null);

/// <summary>What the last check of a source recorded (<c>refdata/sources/{id}</c>).</summary>
public sealed class SourceState
{
    public string? Id { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Url { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? Sha256 { get; set; }
    public DateTimeOffset? CheckedAtUtc { get; set; }
    public DateTimeOffset? ImportedAtUtc { get; set; }

    public static string IdFor(string sourceId) => "refdata/sources/" + sourceId;
}

/// <summary>The bytes of one source version, as named files.</summary>
public sealed class SourceInput
{
    public required string SourceId { get; init; }
    public required string Version { get; init; }
    public string? Url { get; init; }
    public string? Sha256 { get; init; }
    public required IReadOnlyList<SourceFile> Files { get; init; }

    /// <summary>The file whose name matches <paramref name="pattern"/> (BeST's Lambert 2008 rename: match by pattern, never by name).</summary>
    public SourceFile? Find(System.Text.RegularExpressions.Regex pattern) => Files.FirstOrDefault(f => pattern.IsMatch(f.Name));
}

/// <param name="Open">Opens a fresh read-only stream each time.</param>
public sealed record SourceFile(string Name, Func<Stream> Open);

/// <summary>One line of a ledger (§4.3): a source identity and the id it minted, forever.</summary>
/// <param name="Identity">The source identity: <c>nis:45068</c>, <c>nis6:45017A</c>, <c>best-vlg:73243</c>, <c>geonames:2750405</c>.</param>
/// <param name="Id">The document id it minted.</param>
/// <param name="Source">The source that first saw it.</param>
public sealed record LedgerEntry(string Identity, string Id, string Source);

/// <summary>Loads embedded resources as source files.</summary>
public static class EmbeddedSourceFiles
{
    /// <summary>The embedded resources of <paramref name="assembly"/> whose logical name starts with <paramref name="prefix"/>, named without it.</summary>
    public static IReadOnlyList<SourceFile> Under(System.Reflection.Assembly assembly, string prefix)
        => [.. assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new SourceFile(n[prefix.Length..], () => assembly.GetManifestResourceStream(n)
                ?? throw new FileNotFoundException($"Embedded resource {n} is missing."))) ];

    /// <summary>The ledger lines in the embedded <c>Ledger/{cc}.ndjson</c> resources of <paramref name="assembly"/>.</summary>
    public static IEnumerable<LedgerEntry> Ledger(System.Reflection.Assembly assembly)
    {
        foreach (var file in Under(assembly, "Ledger/"))
        {
            using var reader = new StreamReader(file.Open());
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                yield return System.Text.Json.JsonSerializer.Deserialize<LedgerEntry>(line, LedgerJson.Options)
                    ?? throw new InvalidDataException($"An empty ledger line in {file.Name}.");
            }
        }
    }
}

/// <summary>The ledger's line format: <c>{"Identity":"nis:45068","Id":"places/be/nis/45068","Source":"be-refnis"}</c>.</summary>
public static class LedgerJson
{
    public static System.Text.Json.JsonSerializerOptions Options { get; } = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
