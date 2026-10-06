using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Moderation.Services;

namespace MintPlayer.Spark.Moderation;

/// <summary>
/// <c>--spark-init-moderation</c>: prints the <c>security.json</c> rights an application must add for
/// its configured privileges and its moderators. Writes nothing — <c>security.json</c> is the
/// application's authorization model, and the grants are a decision to review, not to paste blindly.
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
        var types = ModeratableTypeNames(SparkAppData.Path(builder.Environment.ContentRootPath, "Model"));
        Console.WriteLine(Render(options, types));
        return true;
    }

    /// <summary>The printed report: a <c>rights</c> array per privilege group, and one for moderators.</summary>
    internal static string Render(SparkModerationOptions options, IReadOnlyList<string> types)
    {
        var rights = new JsonArray();
        var notes = new List<string>();
        if (types.Count == 0)
            notes.Add($"No IModeratable entity type was found in {SparkAppData.Relative("Model")}; content rights are shown for '<Type>'.");
        var targets = types.Count == 0 ? ["<Type>"] : types;

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
                    rights.Add(new JsonObject { ["resource"] = resource, ["groupId"] = privilege.GroupId.ToString(), ["_comment"] = $"privilege {name}" });
            }
        }

        foreach (var action in new[] { ModerationRights.Lock, ModerationRights.Review, ModerationRights.Suspend, ModerationRights.Audit, "Restore", "Purge", "ViewDeleted", "Revert" })
        {
            foreach (var resource in Resources(action, targets))
                rights.Add(new JsonObject { ["resource"] = resource, ["groupId"] = "<moderators group id>", ["_comment"] = "moderators (never earnable)" });
        }

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

    /// <summary>The model names of the entity types in <paramref name="modelFolder"/> whose CLR type implements <see cref="IModeratable"/>.</summary>
    internal static IReadOnlyList<string> ModeratableTypeNames(string modelFolder)
    {
        if (!Directory.Exists(modelFolder))
            return [];
        var names = new List<string>();
        foreach (var file in Directory.EnumerateFiles(modelFolder, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
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
