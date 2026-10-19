using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.ReferenceData.Releases;

/// <summary>
/// The release format (PRD §4.6, RD6): a versioned public contract read by the instance, the .NET client and
/// non-Spark consumers alike. <see cref="SchemaVersion"/> moves with any change to what a line holds.
/// <code>
/// {release}/{cc}/{Collection}.ndjson               full files
/// {release}/deltas/{from}/{cc}/{Collection}.ndjson every document whose Hash changed since {from}
/// {release}/manifest.json  (+ manifest.sig)
/// </code>
/// </summary>
public static class ReleaseFormat
{
    public const int SchemaVersion = 1;

    public const string ManifestFile = "manifest.json";
    public const string SignatureFile = "manifest.sig";

    /// <summary>The collections a release carries, with their document types, in file order.</summary>
    public static IReadOnlyList<ReleaseCollection> Collections { get; } =
    [
        new("RefCountries", typeof(RefCountry)),
        new("RefPlaces", typeof(RefPlace)),
        new("RefPostalCodes", typeof(RefPostalCode)),
        new("RefStreets", typeof(RefStreet)),
        new("RefLineageEvents", typeof(RefLineageEvent)),
        new("RefTimeZones", typeof(RefTimeZone)),
        new("PublicHolidays", typeof(PublicHoliday)),
        new("VatRateSuggestions", typeof(VatRateSuggestion)),
    ];

    public static ReleaseCollection CollectionOf(Type type)
        => Collections.FirstOrDefault(c => c.Type == type) ?? throw new ArgumentException($"{type.Name} is not a release collection.", nameof(type));

    public static ReleaseCollection? CollectionNamed(string name)
        => Collections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The serializer of canonical documents: every property written (null or [] never absent), enum names, dates
    /// <c>yyyy-MM-dd</c>, no indentation, non-ASCII written as is (UTF-8, no BOM).
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = null,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public sealed record ReleaseCollection(string Name, Type Type);

/// <summary>
/// Canonical bytes (PRD §4.6), so the same inputs give the same bytes on every platform: properties sorted by
/// ordinal name at every level, strings in NFC, one document per line sorted by id, LF, UTF-8 without BOM.
/// Set-like arrays are sorted by the builder before they get here (their order is data).
/// </summary>
public static class ReleaseCanonical
{
    private static readonly string[] HashExcluded = ["Hash", "Release"];

    /// <summary>The canonical JSON of <paramref name="document"/>, with <c>@metadata</c> holding only the collection and the CLR type.</summary>
    public static string Serialize(object document, bool withMetadata = true)
    {
        var node = Canonicalize(JsonSerializer.SerializeToNode(document, document.GetType(), ReleaseFormat.JsonOptions));
        if (withMetadata && node is JsonObject obj)
        {
            var collection = ReleaseFormat.CollectionOf(document.GetType());
            var metadata = new JsonObject
            {
                ["@collection"] = collection.Name,
                ["Raven-Clr-Type"] = $"{collection.Type.FullName}, {collection.Type.Assembly.GetName().Name}",
            };
            obj["@metadata"] = metadata;
            node = Canonicalize(obj);
        }
        return node!.ToJsonString(ReleaseFormat.JsonOptions);
    }

    /// <summary><c>sha256:{hex}</c> of the canonical document without <c>Hash</c>, <c>Release</c> and <c>@metadata</c>.</summary>
    public static string Hash(object document)
    {
        var node = JsonSerializer.SerializeToNode(document, document.GetType(), ReleaseFormat.JsonOptions)!.AsObject();
        foreach (var excluded in HashExcluded) node.Remove(excluded);
        var text = Canonicalize(node)!.ToJsonString(ReleaseFormat.JsonOptions);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The digest over (id, Hash) pairs (sorted by id, ordinal) a manifest and <c>refdata/state</c> record per file.</summary>
    public static string Digest(IEnumerable<(string Id, string Hash)> entries)
    {
        var builder = new StringBuilder();
        foreach (var (id, hash) in entries.OrderBy(e => e.Id, StringComparer.Ordinal))
            builder.Append(id).Append('\t').Append(hash).Append('\n');
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary><c>sha256:{hex}</c> of raw bytes (files in the manifest).</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A document read back from a canonical line.</summary>
    public static object Deserialize(string line, ReleaseCollection collection)
    {
        var node = JsonNode.Parse(line)!.AsObject();
        node.Remove("@metadata");
        return node.Deserialize(collection.Type, ReleaseFormat.JsonOptions)
            ?? throw new InvalidDataException($"An empty {collection.Name} line.");
    }

    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => SortObject(obj),
        JsonArray array => new JsonArray([.. array.Select(item => Canonicalize(item?.DeepClone()))]),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(text.Normalize(NormalizationForm.FormC)),
        _ => node?.DeepClone(),
    };

    private static JsonObject SortObject(JsonObject obj)
    {
        var sorted = new JsonObject();
        foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal).ToList())
            sorted[key.Normalize(NormalizationForm.FormC)] = Canonicalize(value);
        return sorted;
    }
}

/// <summary>The manifest of one release (§4.6).</summary>
public sealed class ReleaseManifest
{
    public int SchemaVersion { get; set; } = ReleaseFormat.SchemaVersion;
    public string Release { get; set; } = string.Empty;
    public string? Previous { get; set; }

    /// <summary>The builder that wrote the release: package and version.</summary>
    public string Builder { get; set; } = string.Empty;

    /// <summary>The search-key pipeline version (§4.10): a change re-keys the release, visibly.</summary>
    public int KeyVersion { get; set; }

    public List<ReleaseSource> Sources { get; set; } = [];
    public List<ReleaseFile> Files { get; set; } = [];

    /// <summary>
    /// Deltas from earlier releases (§4.6): for each, every document whose <c>Hash</c> changed since that release, under
    /// <c>deltas/{from}/{cc}/{Collection}.ndjson</c>. Ending is a closed period, so deltas need no tombstones.
    /// </summary>
    public List<ReleaseDelta> Deltas { get; set; } = [];

    /// <summary>Per country: how many documents changed against <see cref="Previous"/>.</summary>
    public Dictionary<string, int> Changes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The next date on which a stored period starts or ends (a merger published in advance).</summary>
    public DateOnly? NextEffectiveDate { get; set; }
}

public sealed class ReleaseSource
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? Sha256 { get; set; }
    public string Licence { get; set; } = string.Empty;
    public string? LicenceUrl { get; set; }
    public string Attribution { get; set; } = string.Empty;
}

public sealed class ReleaseFile
{
    /// <summary>Relative, forward slashes: <c>be/RefPlaces.ndjson</c>.</summary>
    public string Path { get; set; } = string.Empty;
    public string Collection { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public int Count { get; set; }

    /// <summary><see cref="ReleaseCanonical.Digest"/> over the file's (id, Hash) pairs.</summary>
    public string Digest { get; set; } = string.Empty;
}

/// <summary>The delta files from one earlier release (§4.6).</summary>
public sealed class ReleaseDelta
{
    /// <summary>The release the delta starts from: a consumer holding it applies these files instead of the full ones.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Paths <c>deltas/{from}/{cc}/{Collection}.ndjson</c>; their digests cover only the changed documents.</summary>
    public List<ReleaseFile> Files { get; set; } = [];
}

/// <summary>A release whose id set lacks an id of the previous one is refused (§4.6 "Never delete").</summary>
public sealed class ReleaseRefusedException(string message) : Exception(message);
