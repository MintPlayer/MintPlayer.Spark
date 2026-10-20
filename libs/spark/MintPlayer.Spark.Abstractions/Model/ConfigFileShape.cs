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
/// <para>
/// <c>programUnits.json</c> is hashed for its <c>url</c> units' addresses only, each keyed by the unit's id
/// (owner, 2026-10-15: "only include the urls in the modelhash. a normal program-unit change doesn't need to
/// modify the hashes. the only risk is for external links to get tampered with somehow. Then the app mustn't
/// start"). Names, icons, order, groups, the other unit types and comments do not move it, and a unit keyed by
/// its id moves between groups without moving it. It used to walk the groups alone and never reached a unit,
/// so a tampered url started as if nothing had changed.
/// </para>
/// </remarks>
public static class ConfigFileShape
{
    /// <summary>Fields of an action that change what it may do, rather than how it looks.</summary>
    private static readonly string[] StructuralActionFields = ["showedOn", "selectionRule"];

    /// <summary>The unit type whose address is hashed, matched ignoring case as the loader matches it.</summary>
    private const string UrlUnitType = "url";

    /// <summary>The action catalogue's file name, relative to <c>App_Data</c>.</summary>
    public const string ActionsFileName = "actions.json";

    /// <summary>The menu's file name, relative to <c>App_Data</c>.</summary>
    public const string ProgramUnitsFileName = "programUnits.json";

    /// <summary>The files this covers, relative to <c>App_Data</c>.</summary>
    public static readonly string[] FileNames = [ActionsFileName, ProgramUnitsFileName];

    /// <summary>
    /// One structural hash per covered file, keyed by file name. <c>programUnits.json</c> is in the
    /// result only when it exists and its composed menu has a <c>url</c> unit; <c>actions.json</c>
    /// whenever the composed catalogue has an action, since the libraries ship actions whether or not
    /// the application has a file.
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
        Add(ProgramUnitsFileName, SparkLayerKinds.ProgramUnits, DescribeUrlUnits);
        return result;

        void Add(string fileName, string kind, Func<string, string?> describe)
        {
            var shipped = SparkLayerCatalog.Of(list, kind);
            if (shipped.Count == 0) return;

            var layers = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (library, layer) in shipped)
                layers[library.Alias] = Sha256Hex(describe(layer.Shipped) ?? "unparseable\n");

            var path = Path.Combine(appDataPath, fileName);
            if (File.Exists(path))
                layers[SparkLayerProvenance.App] = Sha256Hex(describe(File.ReadAllText(path)) ?? "unparseable\n");
            result[fileName] = layers;
        }
    }

    /// <summary>
    /// The composed menu's rendering (<see cref="SparkProgramUnitsFiles"/>, <see cref="DescribeUrlUnits"/>);
    /// <see langword="null"/> when it does not compose, for the same reason <see cref="DescribeUrlUnits"/> yields null.
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
        return composed is null ? null : DescribeUrlUnits(composed);
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
    /// One <c>programUnits.json</c> rendering (a layer, or the composed menu) as its <c>url</c> units: each one's
    /// id and <c>url</c>, sorted by id, so neither the authoring order nor the group a unit sits in is
    /// structural. Empty when there is none; <see langword="null"/> when the text cannot be parsed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every property is read as the loader reads it: names ignoring case, and the last of two spellings winning
    /// (<c>JsonSerializer</c> with <c>PropertyNameCaseInsensitive</c>), so a <c>"Url"</c> or a <c>"type": "URL"</c>
    /// cannot carry an address past the hash that the menu then serves.
    /// </para>
    /// <para>
    /// Unparseable yields null rather than throwing: a malformed file is refused by its own loader at
    /// startup, in that loader's words. Failing here would replace a specific message with a generic
    /// one, at a point the author has less context.
    /// </para>
    /// </remarks>
    internal static string? DescribeUrlUnits(string json)
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
            var units = new List<(string Id, string? Url)>();
            foreach (var group in Items(Last(document.RootElement, "programUnitGroups")))
            {
                foreach (var unit in Items(Last(group, "programUnits")))
                {
                    if (Last(unit, "type") is not { ValueKind: JsonValueKind.String } type
                        || !string.Equals(type.GetString(), UrlUnitType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Keyed by the id the loader reads, a Guid in one spelling, so its case does not matter either.
                    var id = Last(unit, "id") is { } idValue ? Render(idValue) : string.Empty;
                    units.Add((Guid.TryParse(id, out var guid) ? guid.ToString("D") : id, Last(unit, "url") is { } url ? Render(url) : null));
                }
            }

            var builder = new StringBuilder();
            foreach (var (id, url) in units.OrderBy(u => u.Id, StringComparer.Ordinal).ThenBy(u => u.Url, StringComparer.Ordinal))
            {
                builder.Append(id).Append('\n');
                if (url is not null)
                    builder.Append("  url=").Append(url).Append('\n');
            }
            return builder.ToString();
        }
    }

    /// <summary>The last property of <paramref name="element"/> named <paramref name="name"/>, ignoring case; <see langword="null"/> when it has none or is no object.</summary>
    private static JsonElement? Last(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        JsonElement? found = null;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                found = property.Value;
        }
        return found;
    }

    /// <summary>The items of an array, or none.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement? array)
        => array is { ValueKind: JsonValueKind.Array } items ? items.EnumerateArray() : [];

    /// <summary>
    /// The composed action catalogue's structural rendering: each action's name and the structural
    /// properties it ends up with, sorted. <see langword="null"/> when a layer cannot be composed,
    /// for the same reason <see cref="DescribeUrlUnits"/> yields null.
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
