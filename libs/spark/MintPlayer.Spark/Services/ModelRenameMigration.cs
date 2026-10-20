using MintPlayer.Spark.Abstractions;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The application files a type rename moves (D28, research §8.6), applied by <c>--spark-synchronize-model</c>
/// after the model files have moved, and reviewed in the pull request's diff:
/// <list type="bullet">
/// <item><c>translations.json</c>: the application's <c>model.Old.*</c> keys become <c>model.New.*</c>, nested or dotted.</item>
/// <item><c>security.json</c>: the application's rights on <c>Verb/Old</c> and <c>Verb/Old/Attribute</c> follow the
/// type, <b>only while no type has the old name any more</b>. When another type took it, a right on the old name now
/// means that other type: exactly the case where a right silently changes type. Nothing is rewritten then; the rights
/// are listed to re-target by hand, and the command fails.</item>
/// </list>
/// Then <c>--spark-synchronize-security</c> shows the posture diff, whose type-identity column names the type a
/// right now covers.
/// </summary>
internal static class ModelRenameMigration
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="renames">Old name → new name, in the order the run renamed them.</param>
    /// <param name="names">Every type name of the model after the run (ignoring case).</param>
    /// <returns>False when rights had to be left for the developer.</returns>
    public static bool Apply(string contentRootPath, IReadOnlyList<(string OldName, string NewName)> renames, IReadOnlySet<string> names)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (oldName, newName) in renames)
            if (!string.Equals(oldName, newName, StringComparison.Ordinal)) map[oldName] = newName;
        if (map.Count == 0) return true;

        RekeyTranslations(SparkAppData.Path(contentRootPath, "translations.json"), map);
        return RetargetRights(SparkAppData.Path(contentRootPath, "security.json"), map, names);
    }

    internal static void RekeyTranslations(string path, IReadOnlyDictionary<string, string> map)
    {
        if (!File.Exists(path)) return;
        var original = File.ReadAllText(path);
        if (Parse(original) is not JsonObject root) return;

        var changed = false;
        // Nested: { "model": { "Old": { … } } }.
        if (root["model"] is JsonObject model)
            changed |= RenameMembers(model, key => map.TryGetValue(key, out var renamed) ? renamed : null);
        // Dotted: { "model.Old.label": { … } } or { "model.Old": { … } }.
        changed |= RenameMembers(root, key =>
        {
            if (!key.StartsWith("model.", StringComparison.Ordinal)) return null;
            var rest = key["model.".Length..];
            var dot = rest.IndexOf('.');
            var type = dot < 0 ? rest : rest[..dot];
            return map.TryGetValue(type, out var renamed) ? "model." + renamed + (dot < 0 ? "" : rest[dot..]) : null;
        });

        if (!changed) return;
        Write(path, original, root);
        Console.WriteLine($"Moved the translations of {string.Join(", ", map.Select(p => $"'{p.Key}' -> '{p.Value}'"))} in {path}.");
    }

    internal static bool RetargetRights(string path, IReadOnlyDictionary<string, string> map, IReadOnlySet<string> names)
    {
        if (!File.Exists(path)) return true;
        var original = File.ReadAllText(path);
        if (Parse(original) is not JsonObject root) return true;
        var rights = root.FirstOrDefault(m => string.Equals(m.Key, "rights", StringComparison.OrdinalIgnoreCase)).Value as JsonArray;
        if (rights is null) return true;

        var ignoringCase = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var blocked = new List<string>();
        foreach (var right in rights.OfType<JsonObject>())
        {
            var key = right.FirstOrDefault(m => string.Equals(m.Key, "resource", StringComparison.OrdinalIgnoreCase)).Key;
            if (key is null || right[key] is not JsonValue value || !value.TryGetValue<string>(out var resource)) continue;

            var segments = resource.Split('/');
            if (segments.Length < 2 || !ignoringCase.TryGetValue(segments[1], out var renamed)) continue;
            if (names.Contains(segments[1]))
            {
                blocked.Add($"{resource} (key '{right["key"]?.ToString() ?? "?"}'): '{segments[1]}' is now another type's name; point it at '{renamed}' or keep it for the new '{segments[1]}' deliberately");
                continue;
            }

            segments[1] = renamed;
            right[key] = string.Join("/", segments);
            changed = true;
        }

        if (blocked.Count > 0)
        {
            // Nothing is half-done: either every right of the renamed types moves, or none does.
            Console.Error.WriteLine(
                $"Spark: {path} has rights on a renamed type's old name, which another type now has. A right on that name would " +
                "silently cover the other type, so none was rewritten. Re-target each by hand, then run the command again:"
                + Environment.NewLine + string.Join(Environment.NewLine, blocked.Select(b => "  - " + b)));
            return false;
        }

        if (!changed) return true;
        Write(path, original, root);
        Console.WriteLine($"Moved the rights of {string.Join(", ", map.Select(p => $"'{p.Key}' -> '{p.Value}'"))} in {path}. Run --spark-synchronize-security to review the posture.");
        return true;
    }

    /// <summary>Renames members in place, keeping their order; false when nothing changed.</summary>
    private static bool RenameMembers(JsonObject obj, Func<string, string?> rename)
    {
        var members = obj.ToList();
        if (!members.Any(m => rename(m.Key) is { } renamed && renamed != m.Key)) return false;

        obj.Clear();
        foreach (var (key, value) in members)
            obj[rename(key) ?? key] = value;
        return true;
    }

    private static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null; // Its own reader reports it.
        }
    }

    private static void Write(string path, string original, JsonObject root)
    {
        var lineEnding = original.Contains("\r\n") ? "\r\n" : Environment.NewLine;
        File.WriteAllText(path, root.ToJsonString(WriteOptions).Replace("\r\n", "\n").Replace("\n", lineEnding) + lineEnding);
    }
}
