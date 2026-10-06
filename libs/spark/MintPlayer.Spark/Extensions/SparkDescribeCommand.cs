using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Layering;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark;

/// <summary>
/// <c>--spark-describe &lt;kind&gt; [name] [--layers]</c> (composition D9, grill Q4 = A): prints what a
/// kind composes to — the libraries' compiled layers with the application's files on top — then exits.
/// Opens no database, like the model commands.
/// </summary>
/// <remarks>
/// <para>
/// The only view of the composed result: no composed file is written anywhere (decision log row 10),
/// because a view the build writes goes stale as soon as the application's hot-reloaded files change.
/// This one reads the files as they are now.
/// </para>
/// <para>
/// Without <c>--layers</c> it prints the composed JSON (the rights as a table); with it, one line per
/// leaf and the layer that set it, a library by its alias (D16), the application as <c>app</c>. A name
/// narrows it to one element: an action, a model type, a translation key prefix, a right's key or
/// resource, a program unit or group id, a moderation section.
/// </para>
/// </remarks>
internal static class SparkDescribeCommand
{
    internal const string Flag = "--spark-describe";
    internal const string LayersFlag = "--layers";

    internal static readonly string[] Kinds = ["actions", "model", "translations", "security", "programUnits", "moderation"];

    /// <summary>The kind, name and <c>--layers</c> after <see cref="Flag"/>; <see langword="false"/> when the flag is absent.</summary>
    internal static bool TryParse(string[] args, out string? kind, out string? name, out bool layers)
    {
        kind = name = null;
        layers = args.Contains(LayersFlag);
        var index = Array.IndexOf(args, Flag);
        if (index < 0) return false;

        var operands = args.Skip(index + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        kind = operands.ElementAtOrDefault(0);
        name = operands.ElementAtOrDefault(1);
        return true;
    }

    /// <summary>The text the command prints.</summary>
    /// <exception cref="ArgumentException">The kind is none of <see cref="Kinds"/>.</exception>
    /// <exception cref="InvalidOperationException">The kind's layers do not compose.</exception>
    internal static string Describe(string contentRootPath, string? kind, string? name, bool layers, IReadOnlyList<SparkLibrary> libraries)
    {
        var normalized = Kinds.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase))
            ?? (string.Equals(kind, "rights", StringComparison.OrdinalIgnoreCase) ? "security" : null)
            ?? throw new ArgumentException(
                $"{Flag} needs a kind: {string.Join(", ", Kinds)}" + (kind is null ? "." : $"; '{kind}' is none of them."));

        var aliases = libraries.ToDictionary(l => l.AssemblyName, l => l.Alias, StringComparer.Ordinal);
        string LayerName(string layer) => aliases.TryGetValue(layer, out var alias) ? alias : "app";

        var output = new StringBuilder();
        switch (normalized)
        {
            case "actions":
            {
                var appPath = ActionsCatalogueLoader.PathFor(contentRootPath);
                var stack = SparkLayerCatalog.Of(libraries, SparkLayerKinds.Actions)
                    .Select(x => SparkLayer.Parse(x.Library.AssemblyName, x.Layer.Json, isLibrary: true))
                    .ToList();
                if (File.Exists(appPath))
                    stack.Add(SparkLayer.Parse(SparkActionLayers.AppLayerName, File.ReadAllText(appPath), isLibrary: false));
                Header(output, "actions", stack.Select(l => l.Name), LayerName);
                Render(output, SparkLayers.Compose(stack, SparkKinds.Actions), name, layers, LayerName);
                break;
            }

            case "model":
            {
                var types = SparkModelFiles.Compose(libraries, SparkAppData.Path(contentRootPath, "Model"))
                    .Where(t => name is null || string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t.Name, StringComparer.Ordinal)
                    .ToList();
                if (name is not null && types.Count == 0)
                    throw new ArgumentException($"The composed model has no type named '{name}'.");

                foreach (var type in types)
                {
                    Header(output, $"model {type.Name}", type.Layers, LayerName);

                    // An application's own type is one layer: composed on its own, it still shows as @app.
                    var composition = type.Entry.Composition
                        ?? SparkLayers.Compose([SparkLayer.Parse(type.FileName, type.Json, isLibrary: false)], SparkKinds.Model);
                    Render(output, composition, name: null, layers, LayerName);
                    output.AppendLine();
                }
                break;
            }

            case "translations":
            {
                var appPath = SparkTranslations.PathFor(contentRootPath);
                var inputs = SparkLayerCatalog.Of(libraries, SparkLayerKinds.Translations)
                    .Select(x => new SparkTranslationsInput(x.Library.AssemblyName, x.Layer.Json, isLibrary: true, x.Library.DependsOn))
                    .ToList();
                if (File.Exists(appPath))
                    inputs.Add(new SparkTranslationsInput(SparkTranslations.AppLayerName, File.ReadAllText(appPath), isLibrary: false));
                Header(output, "translations", inputs.Select(i => i.Layer), LayerName);
                Render(output, SparkTranslationLayers.Compose(inputs).Composition, name, layers, LayerName);
                break;
            }

            case "security":
                DescribeSecurity(output, contentRootPath, name, layers, libraries, LayerName);
                break;

            case "programUnits":
            {
                var appPath = SparkProgramUnitsFiles.PathFor(contentRootPath);
                var appJson = File.Exists(appPath) ? File.ReadAllText(appPath) : null;
                Header(output, "programUnits",
                    SparkLayerCatalog.Of(libraries, SparkLayerKinds.ProgramUnits).Select(x => x.Library.AssemblyName)
                        .Concat(appJson is null ? [] : [SparkProgramUnitsFiles.AppLayerName]),
                    LayerName);
                if (SparkProgramUnitsFiles.ComposeLayers(appJson, libraries) is { } composition)
                    Render(output, composition, name, layers, LayerName);
                else
                    output.AppendLine("(no menu: neither a library nor the application states one)");
                break;
            }

            case "moderation":
            {
                var appPath = SparkAppData.Path(contentRootPath, "moderation.json");
                var appJson = File.Exists(appPath) ? File.ReadAllText(appPath) : null;
                Header(output, "moderation",
                    SparkLayerCatalog.Of(libraries, SparkLayerKinds.Moderation).Select(x => x.Library.AssemblyName)
                        .Concat(appJson is null ? [] : [SparkAppData.Relative("moderation.json")]),
                    LayerName);
                Render(output, SparkModerationFiles.ComposeLayers(appJson, SparkAppData.Relative("moderation.json"), libraries), name, layers, LayerName);
                break;
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// The rights as the evaluator reads them: library keys namespaced, tokens and slots resolved, one row
    /// per group, then the inert rights of switched-off libraries and every problem startup would refuse.
    /// </summary>
    private static void DescribeSecurity(StringBuilder output, string contentRootPath, string? name, bool layers, IReadOnlyList<SparkLibrary> libraries, Func<string, string> layerName)
    {
        var appPath = SparkSecurityFiles.PathFor(contentRootPath);
        if (!File.Exists(appPath))
            throw new InvalidOperationException($"There is no {SparkSecurityFiles.AppLayerName} to compose the rights with.");

        var modelTypes = SparkModelFiles.Compose(libraries, SparkAppData.Path(contentRootPath, "Model")).Select(t => t.Name).OfType<string>();
        var composed = SparkSecurityFiles.Compose(File.ReadAllText(appPath), libraries, modelTypes);
        var configuration = composed.Configuration;

        var stack = SparkSecurityFiles.Layers(libraries).Select(l => l.Assembly).Append(SparkSecurityFiles.AppLayerName);
        Header(output, "security (rights)", stack, layerName);

        bool Matches(Right right) => name is null
            || string.Equals(right.Key, name, StringComparison.OrdinalIgnoreCase)
            || right.Resource.Contains(name, StringComparison.OrdinalIgnoreCase);

        string Group(Right right)
        {
            var group = configuration.Groups.FirstOrDefault(g => Guid.TryParse(g.Key, out var id) && id == right.GroupId).Value ?? right.GroupId.ToString();
            return right.Group is { } token && !Guid.TryParse(token, out _) ? $"{group} ({token})" : group;
        }

        string Row(Right right, string group)
            => $"{right.Key} | {right.Resource} | {group} | {(right.IsDenied ? "deny" : "grant")}{(right.IsImportant ? " important" : "")}"
               + (layers ? $" | {right.Layer ?? "app"}" : "");

        output.AppendLine(layers ? "key | resource | group | effect | layer" : "key | resource | group | effect");
        foreach (var right in configuration.Rights.Where(Matches).OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Resource, StringComparer.OrdinalIgnoreCase))
            output.AppendLine(Row(right, Group(right)));

        var inert = configuration.InertRights.Where(Matches).ToList();
        if (inert.Count > 0)
        {
            output.AppendLine();
            output.AppendLine("Inert (libraries switched off in \"libraries\"):");
            foreach (var right in inert.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
                output.AppendLine(Row(right, right.Group ?? right.GroupId.ToString()));
        }

        foreach (var problem in composed.Problems)
            output.AppendLine("problem " + problem);
    }

    private static void Header(StringBuilder output, string what, IEnumerable<string> stack, Func<string, string> layerName)
    {
        var names = stack.Select(layer => layerName(layer) is var alias && alias != "app" ? $"{alias} ({layer})" : $"app ({layer})").ToList();
        output.AppendLine($"{what}, composed from: {(names.Count == 0 ? "(no layer)" : string.Join(" → ", names))}");
    }

    /// <summary>The composed JSON, or (<paramref name="layers"/>) each leaf and its layer; narrowed to <paramref name="name"/>.</summary>
    private static void Render(StringBuilder output, SparkComposition composition, string? name, bool layers, Func<string, string> layerName)
    {
        if (layers)
        {
            var text = composition.Describe(layerName, name is null ? null : path => Selects(path, name));
            if (name is not null && text.Length == 0)
                throw new ArgumentException($"Nothing composed is named '{name}'.");
            output.Append(text);
            return;
        }

        SparkJsonNode? selected = name is null ? composition.Result : Select(composition.Result, name);
        if (selected is null)
            throw new ArgumentException($"Nothing composed is named '{name}'.");
        output.AppendLine(SparkJson.Write(selected, indented: true));

        foreach (var conflict in composition.Conflicts)
            output.AppendLine($"conflict {conflict.PathText}: {layerName(conflict.WinnerLayer)} over {layerName(conflict.LoserLayer)}");
        foreach (var error in composition.Errors)
            output.AppendLine($"error {error.Path} ({layerName(error.Layer)}): {error.Message}");
    }

    /// <summary>
    /// Whether a described path belongs to <paramref name="name"/>: the member itself (<c>Edit</c>), what
    /// is under it (<c>Edit.icon</c>, a translation key prefix <c>model.SparkUser</c>), or a keyed element
    /// anywhere (<c>programUnitGroups[…]</c>).
    /// </summary>
    internal static bool Selects(string path, string name)
        => string.Equals(path, name, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(name + "[", StringComparison.OrdinalIgnoreCase)
           || path.Contains("[" + name + "]", StringComparison.OrdinalIgnoreCase);

    /// <summary>The root members <paramref name="name"/> selects (<see cref="Selects"/>), else the first element anywhere whose id, key or name it is.</summary>
    private static SparkJsonNode? Select(SparkJsonObject root, string name)
    {
        var members = root.Members.Where(m => Selects(m.Key, name)).ToList();
        if (members.Count > 0)
        {
            var selected = new SparkJsonObject(StringComparer.Ordinal);
            foreach (var member in members) selected.Set(member.Key, member.Value);
            return selected;
        }
        return Find(root, name);

        static SparkJsonObject? Find(SparkJsonNode node, string name)
        {
            switch (node)
            {
                case SparkJsonObject obj:
                    foreach (var key in (string[])["id", "key", "name"])
                        if (obj[key] is SparkJsonString { Value: var value } && string.Equals(value, name, StringComparison.OrdinalIgnoreCase))
                            return obj;
                    foreach (var member in obj.Members)
                        if (Find(member.Value, name) is { } found) return found;
                    return null;
                case SparkJsonArray array:
                    foreach (var item in array.Items)
                        if (Find(item, name) is { } found) return found;
                    return null;
                default:
                    return null;
            }
        }
    }
}
