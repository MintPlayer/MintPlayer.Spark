using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeoTester.Catalogue;

/// <summary>
/// The check catalogue as it ships with the application (<c>Catalogue/checks.json</c>, embedded): the seed of the
/// administrator-editable <c>CheckDefinition</c> documents (PRD §3.1, D32). The seeder adds what is new, refreshes
/// what nobody edited, and never overwrites an administrator's edit; "Reset to shipped" restores one entry from here.
/// </summary>
public static class ShippedCatalogue
{
    private const string ResourceName = "SeoTester.Catalogue.checks.json";

    private static readonly Lazy<IReadOnlyList<ShippedCheck>> checks = new(() => Parse(Read()));

    /// <summary>Every shipped check, in file order.</summary>
    public static IReadOnlyList<ShippedCheck> All => checks.Value;

    /// <summary>The shipped check <paramref name="checkId"/>, or null when the application does not ship it.</summary>
    public static ShippedCheck? Find(string checkId) => All.FirstOrDefault(c => string.Equals(c.Id, checkId, StringComparison.Ordinal));

    /// <summary>Parses a catalogue file and refuses one a seeder must not act on (duplicate ids, unknown values).</summary>
    public static IReadOnlyList<ShippedCheck> Parse(string json)
    {
        var file = JsonSerializer.Deserialize<CatalogueFile>(json, Options)
            ?? throw new InvalidDataException("The check catalogue is empty.");
        var list = file.Checks ?? [];
        var duplicate = list.GroupBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"The check catalogue lists '{duplicate.Key}' twice.");
        foreach (var check in list)
        {
            if (!IsCheckId(check.Id))
                throw new InvalidDataException($"The check id '{check.Id}' is not '<category>.<kebab-case>'.");
            if (check.Weight < 0)
                throw new InvalidDataException($"The check '{check.Id}' has a negative weight.");
        }
        return list;
    }

    /// <summary>A check id: <c>{category}.{name}</c>, lower-case ASCII letters, digits and dashes, at most 80 characters.</summary>
    public static bool IsCheckId(string? id)
        => id is { Length: > 2 and <= 80 } && id.Count(c => c == '.') == 1 && !id.StartsWith('.') && !id.EndsWith('.')
           && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.');

    private static string Read()
    {
        using var stream = typeof(ShippedCatalogue).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        ReadCommentHandling = JsonCommentHandling.Disallow,
        WriteIndented = false,
    };

    private sealed class CatalogueFile
    {
        public int Version { get; set; }
        public List<ShippedCheck>? Checks { get; set; }
    }
}

/// <summary>One entry of <c>checks.json</c>.</summary>
public sealed class ShippedCheck
{
    /// <summary><c>{category}.{kebab-case}</c>: the permanent key of the check's C# implementation.</summary>
    public string Id { get; set; } = string.Empty;
    public CheckCategory Category { get; set; }
    public CheckSeverity Severity { get; set; }
    public int Weight { get; set; }

    /// <summary>Where it is measured (research legend: H raw HTML, R rendered, N network, T TLS, D DNS, X external API, S site level).</summary>
    public string Layer { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public LocalizedText Title { get; set; } = new();
    public LocalizedText Summary { get; set; } = new();
    public LocalizedText HowToFix { get; set; } = new();

    /// <summary>Code per platform: <c>nginx</c>, <c>apache</c>, <c>iis</c>, <c>html</c>. Code does not translate.</summary>
    public Dictionary<string, string> Snippets { get; set; } = [];
    public string? DocsUrl { get; set; }

    /// <summary>
    /// A hash of the entry's content. The seeder stores it with what it wrote, so it can tell an entry nobody edited
    /// (refreshed when the shipped text changes) from an administrator's edit (left alone).
    /// </summary>
    public string ContentHash() => CatalogueHash.Of(this);
}

/// <summary>An English and a Dutch text (D9, D16).</summary>
public sealed class LocalizedText
{
    public string En { get; set; } = string.Empty;
    public string Nl { get; set; } = string.Empty;
}

/// <summary>The hash behind <see cref="ShippedCheck.ContentHash"/>, also used for an edited document's content.</summary>
public static class CatalogueHash
{
    /// <summary>SHA-256 over a canonical serialization (fixed property order, ordinal-sorted snippets).</summary>
    public static string Of(ShippedCheck check)
    {
        var canonical = new StringBuilder();
        void Add(string name, string? value) => canonical.Append(name).Append('=').Append(value?.Length ?? -1).Append(':').Append(value).Append('\n');
        Add("category", check.Category.ToString());
        Add("severity", check.Severity.ToString());
        Add("weight", check.Weight.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("layer", check.Layer);
        Add("enabled", check.Enabled ? "1" : "0");
        Add("title.en", check.Title.En);
        Add("title.nl", check.Title.Nl);
        Add("summary.en", check.Summary.En);
        Add("summary.nl", check.Summary.Nl);
        Add("howToFix.en", check.HowToFix.En);
        Add("howToFix.nl", check.HowToFix.Nl);
        foreach (var (platform, code) in check.Snippets.OrderBy(s => s.Key, StringComparer.Ordinal))
            Add("snippet." + platform, code);
        Add("docsUrl", check.DocsUrl);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
