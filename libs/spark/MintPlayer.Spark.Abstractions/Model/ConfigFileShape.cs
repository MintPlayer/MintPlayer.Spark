using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Layering;

namespace MintPlayer.Spark.Abstractions.Model;

/// <summary>
/// Structural fingerprints for the two <c>App_Data</c> files that had no integrity gate at all:
/// <c>actions.json</c> and <c>programUnits.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The model hash globs <c>App_Data/Model/*.json</c> and stops there. <c>security.json</c> has its
/// own mechanism — a committed posture baseline — but these two were covered by nothing, while both
/// carry decisions the runtime enforces: a custom action must be present in the action catalogue
/// to run at all, and its <c>selectionRule</c> bounds how many rows an action may be handed.
/// </para>
/// <para>
/// <b>Structural, not byte-level</b>, following <see cref="ModelFileShape"/>. What is included is
/// what changes behaviour; what is excluded is what changes appearance.
/// </para>
/// <para>
/// <c>actions.json</c> is hashed <b>composed</b> (#467, S12): the libraries' layers with the
/// application's file on top, so a library that changes Delete's rule shows up in verify even though
/// no file of the application changed.
/// </para>
/// </remarks>
public static class ConfigFileShape
{
    /// <summary>Fields of an action that change what it may do, rather than how it looks.</summary>
    private static readonly string[] StructuralActionFields = ["showedOn", "selectionRule"];

    /// <summary>Fields of a program unit that decide where it points.</summary>
    private static readonly string[] StructuralUnitFields = ["type", "queryId", "persistentObjectId", "url"];

    /// <summary>The action catalogue's file name, relative to <c>App_Data</c>.</summary>
    public const string ActionsFileName = "actions.json";

    /// <summary>The menu's file name, relative to <c>App_Data</c>.</summary>
    public const string ProgramUnitsFileName = "programUnits.json";

    /// <summary>The files this covers, relative to <c>App_Data</c>.</summary>
    public static readonly string[] FileNames = [ActionsFileName, ProgramUnitsFileName];

    /// <summary>
    /// One structural hash per covered file, keyed by file name. <c>programUnits.json</c> is in the
    /// result only when it exists; <c>actions.json</c> whenever the composed catalogue has an action,
    /// since the libraries ship actions whether or not the application has a file.
    /// </summary>
    /// <param name="libraries">The library layers; <see cref="SparkActionLayers.Libraries"/> when omitted.</param>
    public static SortedDictionary<string, string> ComputeFileHashes(
        string appDataPath, IReadOnlyList<SparkActionsLayer>? libraries = null)
    {
        // No early return for a missing App_Data: the libraries' actions compose whether or not the
        // application has the directory yet, so the hash must not depend on it existing (#467, D27).
        var results = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var fileName in FileNames)
        {
            var path = Path.Combine(appDataPath, fileName);
            var exists = File.Exists(path);
            var isActions = string.Equals(fileName, ActionsFileName, StringComparison.OrdinalIgnoreCase);
            if (!exists && !isActions)
                continue;

            var describe = isActions
                ? DescribeActions(exists ? File.ReadAllText(path) : null, libraries ?? SparkActionLayers.Libraries)
                : DescribeProgramUnits(File.ReadAllText(path), SparkLayerCatalog.Libraries);
            if (!string.IsNullOrEmpty(describe))
                results[fileName] = Sha256Hex(describe);
        }

        return results;
    }

    /// <summary>
    /// The provenance of the covered files a library states a layer of (composition D7), keyed by file
    /// name: each layer (a library's alias, <c>app</c> for the application's file) and the structural hash
    /// of what that layer alone states. A file only the application states has no entry.
    /// </summary>
    /// <remarks>
    /// A layer is rendered on its own: an action it removes (<c>"Edit": null</c>) is a line of its own, so
    /// a library that starts or stops removing one moves its hash.
    /// </remarks>
    public static SortedDictionary<string, SortedDictionary<string, string>> ComputeLayerHashes(string appDataPath, IEnumerable<SparkLibrary> libraries)
    {
        var list = libraries.ToList();
        var result = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);

        Add(ActionsFileName, SparkLayerKinds.Actions, json => DescribeLayerActions(json));
        Add(ProgramUnitsFileName, SparkLayerKinds.ProgramUnits, json => Describe(json, ProgramUnitsFileName));
        return result;

        void Add(string fileName, string kind, Func<string, string?> describe)
        {
            var shipped = SparkLayerCatalog.Of(list, kind);
            if (shipped.Count == 0) return;

            var layers = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (library, layer) in shipped)
                layers[library.Alias] = Sha256Hex(describe(layer.Json) ?? "unparseable\n");

            var path = Path.Combine(appDataPath, fileName);
            if (File.Exists(path))
                layers[SparkLayerProvenance.App] = Sha256Hex(describe(File.ReadAllText(path)) ?? "unparseable\n");
            result[fileName] = layers;
        }
    }

    /// <summary>
    /// The composed menu's structural rendering (<see cref="SparkProgramUnitsFiles"/>); <see langword="null"/>
    /// when it does not compose, for the same reason <see cref="Describe"/> yields null.
    /// </summary>
    internal static string? DescribeProgramUnits(string appJson, IEnumerable<SparkLibrary> libraries)
    {
        string? composed;
        try
        {
            composed = SparkProgramUnitsFiles.Compose(appJson, libraries);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        return composed is null ? null : Describe(composed, ProgramUnitsFileName);
    }

    /// <summary>One <c>actions.json</c> layer on its own: each action it states, sorted, with the structural properties it states, or <c>removed</c>.</summary>
    internal static string? DescribeLayerActions(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;

            var builder = new StringBuilder();
            foreach (var action in document.RootElement.EnumerateObject()
                         .Where(p => p.Name != "$schema" && !p.Name.StartsWith('_'))
                         .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(action.Name).Append('\n');
                if (action.Value.ValueKind == JsonValueKind.Null)
                {
                    builder.Append("  removed\n");
                    continue;
                }
                if (action.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var field in StructuralActionFields)
                {
                    var property = action.Value.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase));
                    if (property.Value.ValueKind != JsonValueKind.Undefined)
                        builder.Append("  ").Append(field).Append('=').Append(Render(property.Value)).Append('\n');
                }
            }
            return builder.ToString();
        }
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
            var builder = new StringBuilder();
            AppendEntries(builder, document.RootElement, StructuralUnitFields);
            return builder.ToString();
        }
    }

    /// <summary>
    /// The composed action catalogue's structural rendering: each action's name and the structural
    /// properties it ends up with, sorted. <see langword="null"/> when a layer cannot be composed,
    /// for the same reason <see cref="Describe"/> yields null.
    /// </summary>
    internal static string? DescribeActions(string? appJson, IReadOnlyList<SparkActionsLayer> libraries)
    {
        SparkActionsComposition composition;
        try
        {
            var layers = appJson is null
                ? libraries
                : [.. libraries, new SparkActionsLayer(SparkActionLayers.AppLayerName, appJson, IsLibrary: false)];
            composition = SparkActionLayers.Compose(layers);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var action in composition.Actions.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(action.Name).Append('\n');
            foreach (var field in StructuralActionFields)
            {
                if (action.Properties.TryGetValue(field, out var property))
                    builder.Append("  ").Append(field).Append('=').Append(Render(property.Value)).Append('\n');
            }
        }
        return builder.ToString();
    }

    private static string Render(System.Text.Json.Nodes.JsonNode value)
        => value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();

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
