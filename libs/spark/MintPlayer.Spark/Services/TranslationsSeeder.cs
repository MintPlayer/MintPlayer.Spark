using MintPlayer.Spark.Abstractions;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Writes description seeds into the APP's <c>App_Data/translations.json</c> (#467, D5) — the one place
/// model synchronize writes that file. A seed is the English <c>///</c> summary (or <c>[Description]</c>)
/// of a property, written as <c>en</c> under its description key when no layer defines that key yet.
/// </summary>
/// <remarks>
/// <para>
/// Add-only: an existing non-blank <c>en</c> is a human's wording and is never replaced; a blank one
/// asks for the seed again (#424's "seeds, never owns"). "Defined" means the compiled translations
/// (every library plus the app as last built) or the app file on disk, so an edit not yet rebuilt is
/// respected.
/// </para>
/// <para>
/// Formatting (spike S10): the file is rewritten as indented JSON with the relaxed encoder, keeping its
/// line endings and trailing newline. Every app file is already in that form, so a seed changes only
/// the lines it adds. A file in another form is reformatted once, with a notice. Keys are found both
/// nested (<c>"model": { "Car": … }</c>) and dotted (<c>"model.Car": …</c>). A key that would put a
/// child under an existing translation is skipped with a warning, mirroring <c>SPARK_TRANS_002</c>.
/// </para>
/// </remarks>
internal static class TranslationsSeeder
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string PathFor(string contentRootPath)
        => Path.Combine(contentRootPath, "App_Data", "translations.json");

    /// <summary>The seeds that would be written: those whose key no layer defines in <c>en</c>.</summary>
    internal static IReadOnlyList<(string Key, string Text)> Pending(
        string contentRootPath, IEnumerable<(string Key, string Text)> seeds)
    {
        var root = ReadRoot(PathFor(contentRootPath), out _, out _);
        return seeds
            .Where(s => !IsDefinedInEnglish(s.Key, root))
            .GroupBy(s => s.Key, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>Writes the pending seeds. Returns how many were added.</summary>
    internal static int Apply(string contentRootPath, IEnumerable<(string Key, string Text)> seeds)
    {
        var path = PathFor(contentRootPath);
        var root = ReadRoot(path, out var original, out var lineEnding);
        var added = 0;

        foreach (var (key, text) in seeds)
        {
            if (IsDefinedInEnglish(key, root)) continue;
            if (Insert(root, key, text)) added++;
        }

        if (added == 0) return 0;

        var canonical = original is null || Serialize(ReadRoot(original), "\n") == original.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
        if (!canonical)
            Console.WriteLine($"Notice: {path} was not in canonical indented form and has been reformatted once (#467, D5).");

        File.WriteAllText(path, Serialize(root, lineEnding));
        Console.WriteLine($"Seeded {added} description(s) into {path}.");
        return added;
    }

    private static bool IsDefinedInEnglish(string key, JsonObject root)
    {
        if (SparkTranslations.All.TryGetValue(key, out var compiled)
            && compiled.Translations.TryGetValue("en", out var en)
            && !string.IsNullOrWhiteSpace(en))
            return true;

        return Find(root, key) is JsonObject leaf
            && leaf["en"] is JsonValue value
            && value.TryGetValue<string>(out var onDisk)
            && !string.IsNullOrWhiteSpace(onDisk);
    }

    /// <summary>The node at <paramref name="key"/>, matching members nested or dotted.</summary>
    private static JsonNode? Find(JsonObject node, string key)
    {
        foreach (var (name, child) in node)
        {
            if (string.Equals(name, key, StringComparison.Ordinal)) return child;
            if (child is JsonObject obj && key.StartsWith(name + ".", StringComparison.Ordinal)
                && Find(obj, key[(name.Length + 1)..]) is { } found)
                return found;
        }
        return null;
    }

    private static bool Insert(JsonObject root, string key, string text)
    {
        var node = root;
        var rest = key;
        while (true)
        {
            if (node[rest] is JsonObject exactLeaf)
            {
                exactLeaf["en"] = text;
                return true;
            }

            // Descend into the longest existing member that prefixes the remaining path.
            var next = node
                .Where(m => rest.StartsWith(m.Key + ".", StringComparison.Ordinal))
                .OrderByDescending(m => m.Key.Length)
                .FirstOrDefault();

            if (next.Key is null) break;

            if (next.Value is not JsonObject child || IsTranslationLeaf(child))
            {
                Console.WriteLine($"Warning: cannot seed '{key}': '{next.Key}' is already a translation, and a key " +
                                  "cannot have children (SPARK_TRANS_002). Give the element an explicit key instead.");
                return false;
            }

            node = child;
            rest = rest[(next.Key.Length + 1)..];
        }

        // Create the remaining path nested, one object per segment.
        var segments = rest.Split('.');
        foreach (var segment in segments[..^1])
        {
            var created = new JsonObject();
            node[segment] = created;
            node = created;
        }
        node[segments[^1]] = new JsonObject { ["en"] = text };
        return true;
    }

    /// <summary>A translation is an object whose members are all strings (language → text).</summary>
    private static bool IsTranslationLeaf(JsonObject obj)
        => obj.Count > 0 && obj.All(m => m.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String);

    private static JsonObject ReadRoot(string path, out string? original, out string lineEnding)
    {
        original = File.Exists(path) ? File.ReadAllText(path) : null;
        lineEnding = original?.Contains("\r\n") == true ? "\r\n" : Environment.NewLine;
        return original is null ? new JsonObject() : ReadRoot(original);
    }

    private static JsonObject ReadRoot(string text)
        => JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? new JsonObject();

    private static string Serialize(JsonObject root, string lineEnding)
        => root.ToJsonString(WriteOptions).Replace("\r\n", "\n").Replace("\n", lineEnding) + lineEnding;
}
