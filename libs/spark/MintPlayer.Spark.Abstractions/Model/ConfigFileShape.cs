using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MintPlayer.Spark.Abstractions.Model;

/// <summary>
/// Structural fingerprints for the two <c>App_Data</c> files that had no integrity gate at all:
/// <c>customActions.json</c> and <c>programUnits.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The model hash globs <c>App_Data/Model/*.json</c> and stops there. <c>security.json</c> has its
/// own mechanism — a committed posture baseline — but these two were covered by nothing, while both
/// carry decisions the runtime enforces: a custom action must be present in <c>customActions.json</c>
/// to run at all, and its <c>selectionRule</c> bounds how many rows an action may be handed.
/// </para>
/// <para>
/// <b>Structural, not byte-level</b>, following <see cref="ModelFileShape"/>. These files mix
/// security-relevant fields with presentational ones — an action carries a per-language
/// <c>displayName</c> alongside its <c>selectionRule</c> — and a byte hash would fail the gate every
/// time somebody fixed a Dutch label. What is included is what changes behaviour; what is excluded is
/// what changes appearance.
/// </para>
/// </remarks>
public static class ConfigFileShape
{
    /// <summary>Fields of a custom action that change what it may do, rather than how it looks.</summary>
    private static readonly string[] StructuralActionFields = ["showedOn", "selectionRule"];

    /// <summary>Fields of a program unit that decide where it points.</summary>
    private static readonly string[] StructuralUnitFields = ["type", "queryId", "persistentObjectId", "url"];

    /// <summary>The files this covers, relative to <c>App_Data</c>.</summary>
    public static readonly string[] FileNames = ["customActions.json", "programUnits.json"];

    /// <summary>
    /// One structural hash per covered file that exists, keyed by file name. A file that is absent is
    /// simply not in the result — an application need not have either.
    /// </summary>
    public static SortedDictionary<string, string> ComputeFileHashes(string appDataPath)
    {
        var results = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(appDataPath))
            return results;

        foreach (var fileName in FileNames)
        {
            var path = Path.Combine(appDataPath, fileName);
            if (!File.Exists(path))
                continue;

            var describe = Describe(File.ReadAllText(path), fileName);
            if (describe is not null)
                results[fileName] = Sha256Hex(describe);
        }

        return results;
    }

    /// <summary>
    /// The canonical structural rendering of one file, or <see langword="null"/> when it cannot be
    /// parsed.
    /// </summary>
    /// <remarks>
    /// Unparseable yields null rather than throwing: a malformed file is refused by its own loader at
    /// startup, in that loader's words. Failing here would replace a specific message with a generic
    /// one, at a point the author has less context.
    /// </remarks>
    internal static string? Describe(string json, string fileName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var fields = string.Equals(fileName, "customActions.json", StringComparison.OrdinalIgnoreCase)
                ? StructuralActionFields
                : StructuralUnitFields;

            var builder = new StringBuilder();
            AppendEntries(builder, document.RootElement, fields);
            return builder.ToString();
        }
    }

    /// <summary>
    /// Walks the entries — an object keyed by name for custom actions, or an array of units — and
    /// renders each entry's identity plus its structural fields, sorted so authoring order is not
    /// structural.
    /// </summary>
    private static void AppendEntries(StringBuilder builder, JsonElement root, string[] fields)
    {
        var entries = new List<(string Key, JsonElement Value)>();

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                // A units file nests its list under a property; an actions file is the map itself.
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var item in property.Value.EnumerateArray())
                        entries.Add((property.Name + "[" + Identity(item, index++) + "]", item));
                }
                else if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    entries.Add((property.Name, property.Value));
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in root.EnumerateArray())
                entries.Add((Identity(item, index++), item));
        }

        foreach (var (key, value) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            builder.Append(key).Append('\n');
            foreach (var field in fields)
            {
                if (value.ValueKind == JsonValueKind.Object
                    && value.TryGetProperty(field, out var fieldValue))
                {
                    builder.Append("  ").Append(field).Append('=').Append(Render(fieldValue)).Append('\n');
                }
            }
        }
    }

    /// <summary>A stable name for an array entry: its own name/id if it has one, else its position.</summary>
    private static string Identity(JsonElement element, int index)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in (string[])["name", "id", "title"])
            {
                if (element.TryGetProperty(candidate, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? index.ToString();
            }
        }
        return index.ToString();
    }

    private static string Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.GetRawText(),
    };

    private static string Sha256Hex(string text)
        => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(new UTF8Encoding(false).GetBytes(text)));
}
