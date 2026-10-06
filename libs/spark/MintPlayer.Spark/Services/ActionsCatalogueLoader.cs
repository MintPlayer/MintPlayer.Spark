using System.Text.Json;
using System.Text.Json.Nodes;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Models;

namespace MintPlayer.Spark.Services;

public interface IActionsCatalogueLoader
{
    /// <summary>The composed catalogue: the libraries' <c>actions.json</c> layers with the application's on top.</summary>
    ActionsCatalogue GetCatalogue();

    /// <summary>Composes the catalogue again now; the previous one stays when it does not compose.</summary>
    void Reload();
}

/// <summary>
/// Composes the action catalogue (#467, D7/S12): the libraries' compiled <c>actions.json</c> layers,
/// core first, with the application's <c>App_Data/actions.json</c> on top. The application's file is
/// read from disk and reloaded when it changes, and the labels follow a translations reload, through
/// the one watcher policy (<see cref="AppLayerSnapshot{T}"/>, composition D8); the library layers are
/// fixed at build time.
/// </summary>
[Register(typeof(IActionsCatalogueLoader), ServiceLifetime.Singleton)]
internal partial class ActionsCatalogueLoader : IActionsCatalogueLoader, IDisposable
{
    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly ILogger<ActionsCatalogueLoader> logger;
    [Inject] private readonly ITranslationsLoader translationsLoader;

    private AppLayerSnapshot<ActionsCatalogue>? layer;

    private AppLayerSnapshot<ActionsCatalogue> Layer
        => LazyInitializer.EnsureInitialized(ref layer, () => new(
            SparkActionLayers.AppLayerName,
            Path.GetDirectoryName(PathFor(hostEnvironment.ContentRootPath)),
            [ConfigFileShape.ActionsFileName],
            LoadFromDisk,
            logger,
            labels: translationsLoader));

    public ActionsCatalogue GetCatalogue() => Layer.Current;

    public void Reload() => Layer.Reload();

    private ActionsCatalogue LoadFromDisk()
    {
        var fullPath = PathFor(hostEnvironment.ContentRootPath);
        var appJson = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;

        var catalogue = Build(appJson, SparkActionLayers.Libraries, translationsLoader.GetAll());
        foreach (var conflict in catalogue.Conflicts)
        {
            logger.LogWarning(
                "Libraries '{Winner}' and '{Loser}' both state '{Property}' of the action '{Action}', with different values. "
                + "'{Winner}' wins (libraries apply by assembly name). State it in {ActionsFile} to choose.",
                conflict.WinnerLayer, conflict.LoserLayer, conflict.Property, conflict.Action, conflict.WinnerLayer, SparkActionLayers.AppLayerName);
        }
        logger.LogInformation("Composed the action catalogue: {ActionCount} actions", catalogue.Actions.Count);
        return catalogue;
    }

    /// <summary>The application layer's path for a content root.</summary>
    public static string PathFor(string contentRootPath)
        => SparkAppData.Path(contentRootPath, ConfigFileShape.ActionsFileName);

    /// <summary>The library layers composed with <paramref name="appJson"/> (null: no application file), bound and validated.</summary>
    /// <param name="translations">The texts the labels resolve against; <see langword="null"/>: none, so every label is the humanized name.</param>
    /// <exception cref="FormatException">The composed catalogue is invalid; the message names every offender.</exception>
    internal static ActionsCatalogue Build(string? appJson, IReadOnlyList<SparkActionsLayer> libraries, IReadOnlyDictionary<string, TranslatedString>? translations = null)
    {
        var layers = appJson is null
            ? libraries
            : [.. libraries, new SparkActionsLayer(SparkActionLayers.AppLayerName, appJson, IsLibrary: false)];

        SparkActionsComposition composition;
        try
        {
            composition = SparkActionLayers.Compose(layers);
        }
        catch (InvalidOperationException ex)
        {
            throw new FormatException(ex.Message, ex);
        }

        var problems = new List<string>();
        var actions = composition.Actions
            .Select(action => Bind(action, translations ?? NoTranslations, problems))
            .ToList();

        if (problems.Count > 0)
            throw new FormatException(
                $"The action catalogue is invalid ({problems.Count} problem(s)): {string.Join("; ", problems)}.");

        return new ActionsCatalogue(actions, composition.Conflicts);
    }

    internal static readonly string[] KnownProperties =
        ["label", "description", "confirmation", "icon", "showedOn", "selectionRule", "refreshOnCompleted", "variant", "offset"];

    private static readonly IReadOnlyDictionary<string, TranslatedString> NoTranslations = new Dictionary<string, TranslatedString>();

    private static ActionDefinition Bind(SparkComposedAction action, IReadOnlyDictionary<string, TranslatedString> translations, List<string> problems)
    {
        var name = action.Name;
        var where = $"'{name}' ({action.DeclaredBy})";

        foreach (var (property, value) in action.Properties)
        {
            if (KnownProperties.Contains(property, StringComparer.OrdinalIgnoreCase)) continue;
            problems.Add(property.ToLowerInvariant() switch
            {
                "displayname" => $"{where} states 'displayName': the label is the translations.json key 'actions.{name}.label' (or an explicit 'label' key)",
                "confirmationmessagekey" => $"{where} states 'confirmationMessageKey': write 'confirmation' with the key, or define 'actions.{name}.confirmation' in translations.json",
                _ => $"{where} states the unknown property '{property}' (from {value.Layer}); known: {string.Join(", ", KnownProperties)}",
            });
        }

        string? Text(string property)
        {
            if (!action.Properties.TryGetValue(property, out var p)) return null;
            if (p.Value.GetValueKind() == JsonValueKind.String) return p.Value.GetValue<string>();
            problems.Add(p.Value.GetValueKind() == JsonValueKind.Object
                ? $"{where} embeds translated text in '{property}' (from {p.Layer}). Move it into translations.json under 'actions.{name}.{property}' and remove it from the file, or write the key it should use"
                : $"{where}: '{property}' must be a translations.json key (from {p.Layer})");
            return null;
        }

        string? String(string property)
        {
            if (!action.Properties.TryGetValue(property, out var p)) return null;
            if (p.Value.GetValueKind() == JsonValueKind.String) return p.Value.GetValue<string>();
            problems.Add($"{where}: '{property}' must be a string (from {p.Layer})");
            return null;
        }

        var showedOn = (String("showedOn") ?? "both").ToLowerInvariant();
        if (showedOn is not ("detail" or "query" or "both"))
        {
            problems.Add($"{where} declares showedOn '{showedOn}'; it must be 'detail', 'query' or 'both'");
            showedOn = "both";
        }

        var selectionRule = String("selectionRule");
        if (!SelectionRuleParser.IsValid(selectionRule))
        {
            problems.Add($"{where} declares selectionRule '{selectionRule}'. A rule is a cardinality expression over the number "
                + "of selected rows, such as '=1', '>0', '<=5' or '1<X<5'; set it to null to require no selection");
            selectionRule = null;
        }

        var refreshOnCompleted = false;
        if (action.Properties.TryGetValue("refreshOnCompleted", out var refresh))
        {
            if (refresh.Value.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                refreshOnCompleted = refresh.Value.GetValue<bool>();
            else
                problems.Add($"{where}: 'refreshOnCompleted' must be true or false (from {refresh.Layer})");
        }

        var offset = 0;
        if (action.Properties.TryGetValue("offset", out var offsetProperty))
        {
            if (offsetProperty.Value.GetValueKind() == JsonValueKind.Number && offsetProperty.Value.AsValue().TryGetValue(out int o))
                offset = o;
            else
                problems.Add($"{where}: 'offset' must be a whole number (from {offsetProperty.Layer})");
        }

        // Text is resolved here, once, on the server (#467, D26): the wire carries a TranslatedString.
        var labelKey = Text("label");
        var label = SparkText.Resolve(translations, labelKey is null ? null : TranslatedString.FromKey(labelKey), $"actions.{name}.label", name)!;

        var descriptionKey = Text("description");
        var description = SparkText.Lookup(translations, descriptionKey ?? $"actions.{name}.description");

        TranslatedString? confirmation;
        if (action.Properties.TryGetValue("confirmation", out var confirm) && confirm.Value.GetValueKind() == JsonValueKind.False)
        {
            // An explicit false: never ask, even when the conventional key is translated.
            confirmation = null;
        }
        else
        {
            var confirmationKey = Text("confirmation");
            confirmation = SparkText.Lookup(translations, confirmationKey ?? $"actions.{name}.confirmation")
                // An explicit key asks for a confirmation even before somebody translates it.
                ?? (confirmationKey is null ? null : SparkText.Lookup(translations, "common.areYouSure") ?? TranslatedString.Create("Are you sure?"));
        }

        return new ActionDefinition
        {
            Name = name,
            Label = label,
            Description = description,
            Icon = String("icon"),
            ShowedOn = showedOn,
            SelectionRule = string.IsNullOrEmpty(selectionRule) ? null : selectionRule,
            RefreshOnCompleted = refreshOnCompleted,
            Confirmation = confirmation,
            Variant = String("variant"),
            Offset = offset,
            DeclaredBy = action.DeclaredBy,
            Sources = action.Properties.ToDictionary(p => p.Key, p => p.Value.Layer, StringComparer.OrdinalIgnoreCase),
        };
    }

    [NoInterfaceMember]
    public void Dispose() => layer?.Dispose();
}
