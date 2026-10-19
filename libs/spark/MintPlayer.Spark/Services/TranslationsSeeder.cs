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
/// asks for the seed again (#424's "seeds, never owns"). "Defined" means the composed translations
/// (every library layer plus the app file on disk, composition D10), or the file as this run has
/// already seeded it.
/// </para>
/// <para>
/// Formatting (spike S10): the file is rewritten as indented JSON with the relaxed encoder, keeping its
/// line endings and trailing newline. Every app file is already in that form, so a seed changes only
/// the lines it adds. A file in another form is reformatted once, with a notice. Keys are found both
/// nested (<c>"model": { "Car": … }</c>) and dotted (<c>"model.Car": …</c>). A key that would put a
/// child under an existing translation is skipped with a warning, mirroring <c>SPARK_TRANS_002</c>.
/// </para>
/// <para>
/// Order (<c>docs/coverage_merge_queue_PRD.md</c> O4): every object's members are kept in ordinal key order,
/// so a seed lands among its siblings rather than at the end, and two branches that seed different keys
/// change different lines. A file whose order is already right and that gains no seed is not rewritten.
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
        => SparkAppData.Path(contentRootPath, "translations.json");

    /// <summary>The seeds that would be written: those whose key no layer defines in <c>en</c>.</summary>
    internal static IReadOnlyList<(string Key, string Text)> Pending(
        string contentRootPath, IEnumerable<(string Key, string Text)> seeds)
    {
        var root = ReadRoot(PathFor(contentRootPath), out _, out _);
        var composed = SparkTranslations.Compose(contentRootPath).All;
        return seeds
            .Where(s => !IsDefinedInEnglish(s.Key, composed, root))
            .GroupBy(s => s.Key, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>Writes the pending seeds. Returns how many were added.</summary>
    internal static int Apply(string contentRootPath, IEnumerable<(string Key, string Text)> seeds)
    {
        var path = PathFor(contentRootPath);
        var root = ReadRoot(path, out var original, out var lineEnding);
        var composed = SparkTranslations.Compose(contentRootPath).All;
        var added = 0;

        foreach (var (key, text) in seeds)
        {
            if (IsDefinedInEnglish(key, composed, root)) continue;
            if (Insert(root, key, text)) added++;
        }

        // Sorting is what puts a seed at its place among its siblings, and running it every time makes the
        // order a fixed point: a key a human appended is moved on the next run (docs/coverage_merge_queue_PRD.md O4).
        var reordered = Sort(root);
        if (added == 0 && !reordered) return 0;

        var canonical = original is null || Serialize(ReadRoot(original), "\n") == original.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
        if (!canonical)
            Console.WriteLine($"Notice: {path} was not in canonical indented form and has been reformatted once (#467, D5).");

        File.WriteAllText(path, Serialize(root, lineEnding));
        if (added > 0)
            Console.WriteLine($"Seeded {added} description(s) into {path}.");
        else
            Console.WriteLine($"Sorted the keys of {path}.");
        return added;
    }

    /// <summary>
    /// Puts the members of every object in ordinal key order, <c>$schema</c> first. Returns whether
    /// anything moved.
    /// </summary>
    /// <remarks>
    /// Ordinal rather than culture order, so the result does not depend on the machine that ran it.
    /// Language members inside a translation are sorted too; <c>en</c>, <c>fr</c>, <c>nl</c> already are.
    /// </remarks>
    internal static bool Sort(JsonObject node)
    {
        var moved = false;
        foreach (var (_, child) in node)
            if (child is JsonObject obj && Sort(obj))
                moved = true;

        var members = node.ToList();
        var sorted = members
            .OrderBy(m => m.Key == "$schema" ? 0 : 1)
            .ThenBy(m => m.Key, StringComparer.Ordinal)
            .ToList();
        if (members.Select(m => m.Key).SequenceEqual(sorted.Select(m => m.Key), StringComparer.Ordinal))
            return moved;

        node.Clear();
        foreach (var (name, child) in sorted)
            node.Add(name, child);
        return true;
    }

    private static bool IsDefinedInEnglish(string key, IReadOnlyDictionary<string, TranslatedString> composed, JsonObject root)
    {
        if (composed.TryGetValue(key, out var compiled)
            && !string.IsNullOrWhiteSpace(compiled["en"]))
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
