using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Moderation.Services;

namespace MintPlayer.Spark.Moderation;

/// <summary>
/// <c>--spark-init-moderation</c>: prints the <c>security.json</c> rights an application must add for
/// its configured privileges and its moderators. Writes nothing — <c>security.json</c> is the
/// application's authorization model, and the grants are a decision to review, not to paste blindly.
/// The rights the library ships itself (the <c>Moderation</c> pseudo-type's, composition M9) are not
/// printed: they need only the slot bindings.
/// </summary>
public static class SparkModerationInitExtensions
{
    internal const string InitFlag = "--spark-init-moderation";

    /// <summary>
    /// Content-type actions: granted per moderatable type (<c>Vote/Question</c>). Every reserved verb
    /// (<c>SparkReservedActions</c> — core's, SoftDelete's, History's, Moderation's own) except the
    /// three that name the <see cref="ModerationRights.Target"/> pseudo-type. The earnable lists are
    /// unioned in so a verb whose package is not loadable here still renders per type.
    /// </summary>
    private static readonly Lazy<HashSet<string>> typeActions = new(() =>
    {
        var actions = new HashSet<string>(Spark.Services.SparkReservedActionRegistry.All.Select(a => a.Verb), StringComparer.OrdinalIgnoreCase);
        actions.UnionWith(ModerationRights.NeverEarnable);
        actions.UnionWith(ModerationRights.DefaultEarnable);
        actions.ExceptWith([ModerationRights.Review, ModerationRights.Suspend, ModerationRights.Audit]);
        return actions;
    });

    private static HashSet<string> TypeActions => typeActions.Value;

    /// <summary>
    /// Handles <c>--spark-init-moderation</c>; returns <see langword="true"/> when it did and the host
    /// should return from <c>Main</c>.
    /// </summary>
    /// <example><code>if (builder.InitializeSparkModerationIfRequested(args)) return;</code></example>
    public static bool InitializeSparkModerationIfRequested(this WebApplicationBuilder builder, string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!args.Contains(InitFlag))
            return false;

        builder.Configuration.AddSparkModerationFile();
        var options = new SparkModerationOptions();
        builder.Configuration.GetSection(SparkModerationConfigurationExtensions.SectionName).Bind(options);
        var types = ModeratableTypeNames(SparkModelFiles.Compose(builder.Environment.ContentRootPath).Select(t => t.Json));
        var shipped = OptedOut(SparkSecurityFiles.PathFor(builder.Environment.ContentRootPath)) ? new HashSet<string>() : ShippedRights();
        Console.WriteLine(Render(options, types, shipped));
        return true;
    }

    /// <summary>The library's alias (composition D16): the prefix of its slots and of its rights' keys.</summary>
    internal const string Alias = "moderation";

    /// <summary>The slot the moderators' rights are granted to; the application binds it to its moderators group.</summary>
    internal const string ModeratorsSlot = "moderation:moderators";

    /// <summary>
    /// The rights this library's own <c>security.json</c> layer ships (composition D4, M9), as
    /// <c>resource|groupId</c>, read from the process's library catalogue so the report cannot disagree
    /// with what is composed. Only pseudo-type grants to its own slots can ship: a per-type grant
    /// (<c>Vote/Question</c>) names an application type, which a library may not grant on.
    /// </summary>
    internal static IReadOnlySet<string> ShippedRights(IEnumerable<SparkLibrary>? libraries = null)
    {
        var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var json = (libraries ?? SparkLayerCatalog.Libraries)
            .FirstOrDefault(l => string.Equals(l.Alias, Alias, StringComparison.Ordinal))?
            .Layers.FirstOrDefault(l => l.Kind == "security")?.Json;
        if (json is null)
            return shipped;

        using var document = JsonDocument.Parse(json);
        if (TryGet(document.RootElement, "rights", out var rights) && rights.ValueKind == JsonValueKind.Array)
        {
            foreach (var right in rights.EnumerateArray())
            {
                if (TryGet(right, "resource", out var resource) && TryGet(right, "groupId", out var group))
                    shipped.Add(Shipped(resource.GetString(), group.GetString()));
            }
        }
        return shipped;
    }

    /// <summary>Whether the application's <c>security.json</c> switches this library's rights off (<c>"libraries": { "moderation": false }</c>).</summary>
    private static bool OptedOut(string securityPath)
    {
        if (!File.Exists(securityPath))
            return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(securityPath));
            return TryGet(document.RootElement, "libraries", out var libraries)
                && TryGet(libraries, Alias, out var on)
                && on.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            // Startup reports a broken file; the report just assumes the library's rights apply.
            return false;
        }
    }

    private static string Shipped(string? resource, string? group) => $"{resource}|{group}";

    /// <summary>
    /// The printed report: a <c>rights</c> array per privilege group, and one for moderators. A right in
    /// <paramref name="shipped"/> is left out and named in a note: the library already grants it, and an
    /// application copy would be refused as an edit of a library grant (SPARK049) once keyed alike, or
    /// would duplicate it otherwise.
    /// </summary>
    internal static string Render(SparkModerationOptions options, IReadOnlyList<string> types, IReadOnlySet<string> shipped)
    {
        var rights = new JsonArray();
        var notes = new List<string>();
        var fromLibrary = new List<string>();
        if (types.Count == 0)
            notes.Add($"No IModeratable entity type was found in {SparkAppData.Relative("Model")}; content rights are shown for '<Type>'.");
        var targets = types.Count == 0 ? ["<Type>"] : types;

        void Add(string key, string resource, string group, string comment)
        {
            if (shipped.Contains(Shipped(resource, group)))
                fromLibrary.Add($"{resource} to {group}");
            else
                rights.Add(new JsonObject { ["key"] = key, ["resource"] = resource, ["groupId"] = group, ["_comment"] = comment });
        }

        foreach (var (name, privilege) in options.Privileges)
        {
            if (privilege.Grants.Count == 0)
                notes.Add($"Privilege '{name}' lists no Grants; add e.g. \"Grants\": [\"Vote\"] to moderation.json.");
            foreach (var action in privilege.Grants)
            {
                if (ModerationRights.NeverEarnable.Contains(action))
                {
                    notes.Add($"Privilege '{name}' lists '{action}', which is never earnable; it is left out.");
                    continue;
                }
                foreach (var resource in Resources(action, targets))
                    Add($"moderation-{name}-{resource}", resource, string.IsNullOrWhiteSpace(privilege.Group) ? privilege.GroupId.ToString() : privilege.Group, $"privilege {name}");
            }
        }

        foreach (var action in new[] { ModerationRights.Lock, ModerationRights.Review, ModerationRights.Suspend, ModerationRights.Audit, "Restore", "Purge", "ViewDeleted", "Revert" })
        {
            foreach (var resource in Resources(action, targets))
                Add($"moderators-{resource}", resource, ModeratorsSlot, "moderators (never earnable)");
        }

        if (fromLibrary.Count > 0)
        {
            notes.Add(
                $"Shipped by the library, so not listed: {string.Join(", ", fromLibrary)}. " +
                $"Bind each slot in \"bindings\" instead of copying them; \"libraries\": {{ \"{Alias}\": false }} switches them off.");
        }
        notes.Add($"Bind \"{ModeratorsSlot}\" to your moderators group in \"bindings\"; every privilege's slot is bound the same way.");

        var report = new JsonObject
        {
            ["_comment"] = $"Rights to add to {SparkAppData.Relative("security.json")} for Spark Moderation. Review them; nothing was written.",
            ["notes"] = new JsonArray(notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            ["rights"] = rights,
        };
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static IEnumerable<string> Resources(string action, IReadOnlyList<string> types)
        => TypeActions.Contains(action)
            ? types.Select(t => $"{action}/{t}")
            : [$"{action}/{ModerationRights.Target}"];

    /// <summary>The model names of the entity types among <paramref name="modelFiles"/> (the composed model, composition D6) whose CLR type implements <see cref="IModeratable"/>.</summary>
    internal static IReadOnlyList<string> ModeratableTypeNames(IEnumerable<string> modelFiles)
    {
        var names = new List<string>();
        foreach (var json in modelFiles)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (!TryGet(document.RootElement, "persistentObject", out var po)
                    || !TryGet(po, "name", out var name) || !TryGet(po, "clrType", out var clr))
                    continue;
                var type = ModerationTargets.ResolveType(clr.GetString());
                if (type is not null && typeof(IModeratable).IsAssignableFrom(type) && name.GetString() is { } n)
                    names.Add(n);
            }
            catch (JsonException)
            {
                // Not a model file.
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
