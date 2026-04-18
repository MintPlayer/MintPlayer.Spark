using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Json;

internal enum TranslationsIssueKind
{
    MixedLeafAndNamespace,
    EmptyObject,
    ArrayNotAllowed,
}

internal sealed class TranslationsIssue
{
    public TranslationsIssueKind Kind { get; set; }
    public string Path { get; set; } = "";
}

/// <summary>
/// Walks a parsed translations.json tree and produces a flat list of
/// (dottedKey, languageMap) entries. Diagnostic issues are collected rather
/// than thrown so the caller can attribute them to the AdditionalText.
/// </summary>
internal static class TranslationsTreeFlattener
{
    public static (List<KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>>> Entries, List<TranslationsIssue> Issues) Flatten(JsonNode root)
    {
        var entries = new List<KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>>>();
        var issues = new List<TranslationsIssue>();

        if (root is JsonObject obj)
            Walk(obj, "", entries, issues);
        // A top-level string or null is meaningless; silently produce nothing.

        return (entries, issues);
    }

    private static void Walk(JsonObject obj, string path,
        List<KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>>> entries,
        List<TranslationsIssue> issues)
    {
        if (obj.Members.Count == 0)
        {
            issues.Add(new TranslationsIssue { Kind = TranslationsIssueKind.EmptyObject, Path = path });
            return;
        }

        var allStrings = true;
        var allObjects = true;
        foreach (var m in obj.Members)
        {
            if (m.Value is JsonString) allObjects = false;
            else if (m.Value is JsonObject) allStrings = false;
            else { allStrings = false; allObjects = false; }
        }

        if (allStrings)
        {
            // Leaf — emit a TranslatedString entry at this path.
            var langs = new List<KeyValuePair<string, string>>();
            foreach (var m in obj.Members)
            {
                var lang = m.Key;
                var val = ((JsonString)m.Value).Value;
                langs.Add(new KeyValuePair<string, string>(lang, val));
            }
            entries.Add(new KeyValuePair<string, IReadOnlyList<KeyValuePair<string, string>>>(path, langs));
            return;
        }

        if (allObjects)
        {
            foreach (var m in obj.Members)
            {
                var childPath = string.IsNullOrEmpty(path) ? m.Key : path + "." + m.Key;
                Walk((JsonObject)m.Value, childPath, entries, issues);
            }
            return;
        }

        // Mixed — fail this subtree, keep walking others we can handle.
        issues.Add(new TranslationsIssue { Kind = TranslationsIssueKind.MixedLeafAndNamespace, Path = path });
    }
}
