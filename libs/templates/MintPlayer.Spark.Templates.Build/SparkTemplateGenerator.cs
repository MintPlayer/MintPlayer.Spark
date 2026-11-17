using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MintPlayer.Spark.Templates.Build;

/// <summary>The four template files the generator completes.</summary>
public sealed record TemplateFiles(string TemplateJson, string IdeHostJson, string CliHostJson, string HostCsproj);

/// <summary>
/// Completes the hand-written spark-app template files with the features of <see cref="FeatureManifest"/>:
/// a bool parameter and a <c>Use{Id}</c> computed symbol per feature in template.json, its entry in ide.host.json and
/// dotnetcli.host.json, and its PackageReference in the host csproj (at <see cref="PackageReferencesMarker"/>).
/// </summary>
public static class SparkTemplateGenerator
{
    /// <summary>The line in content/SparkApp/SparkApp/SparkApp.csproj the feature PackageReferences replace.</summary>
    public const string PackageReferencesMarker = "<!-- spark-features: PackageReferences -->";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static TemplateFiles Generate(FeatureManifest manifest, TemplateFiles hand)
    {
        var template = JsonNode.Parse(hand.TemplateJson)!.AsObject();
        var symbols = template["symbols"]!.AsObject();
        Validate(manifest, symbols);

        foreach (var feature in manifest.Features.Where(f => !f.IsHandWritten))
            symbols[feature.Id] = Parameter(manifest, feature);
        foreach (var feature in manifest.Features.Where(f => !f.IsHandWritten))
            symbols[feature.Condition] = new JsonObject { ["type"] = "computed", ["value"] = UseExpression(manifest, feature) };

        var ide = JsonNode.Parse(hand.IdeHostJson)!.AsObject();
        var ideSymbols = ide["symbolInfo"]!.AsArray();
        var firstHidden = ideSymbols.Select((s, i) => (s, i)).FirstOrDefault(x => x.s?["isVisible"]?.GetValue<bool>() == false);
        var insertAt = firstHidden.s is null ? ideSymbols.Count : firstHidden.i;
        foreach (var feature in manifest.Features.Where(f => !f.IsHandWritten))
            ideSymbols.Insert(insertAt++, new JsonObject { ["id"] = feature.Id, ["isVisible"] = true });

        var cli = JsonNode.Parse(hand.CliHostJson)!.AsObject();
        var cliSymbols = cli["symbolInfo"]!.AsObject();
        foreach (var feature in manifest.Features.Where(f => !f.IsHandWritten))
            cliSymbols[feature.Id] = new JsonObject { ["longName"] = feature.Flag, ["shortName"] = "" };

        return new TemplateFiles(
            template.ToJsonString(WriteOptions),
            ide.ToJsonString(WriteOptions),
            cli.ToJsonString(WriteOptions),
            PackageReferences(manifest, hand.HostCsproj));
    }

    /// <summary>
    /// <c>Use{X}</c>: X's own constraints, and X chosen or switched on by any feature that requires it, directly or
    /// not (D16: prerequisites are switched on, not refused). A dependent counts only where its own constraints hold.
    /// </summary>
    public static string UseExpression(FeatureManifest manifest, Feature feature)
    {
        var terms = new List<string> { $"({feature.Id} && true)" };
        terms.AddRange(Dependents(manifest, feature).Select(d => d.IsHandWritten ? d.Symbol! : $"({Guarded(d, $"({d.Id} && true)")})"));
        return $"({Guarded(feature, terms.Count == 1 ? terms[0] : $"({string.Join(" || ", terms)})")})";
    }

    private static string Guarded(Feature feature, string term)
    {
        var guards = Constraints(feature).ToList();
        return guards.Count == 0 ? term : $"{string.Join(" && ", guards)} && {term}";
    }

    private static IEnumerable<string> Constraints(Feature feature)
    {
        if (feature.Store == "RavenDB") yield return "IsRaven";
        if (feature.Store == "InMemory") yield return "IsInMemory";
        if (feature.NeedsClient) yield return "HasClient";
    }

    /// <summary>Every feature whose requires-closure contains <paramref name="feature"/>, in manifest order.</summary>
    public static IEnumerable<Feature> Dependents(FeatureManifest manifest, Feature feature)
        => manifest.Features.Where(f => f != feature && Closure(manifest, f).Contains(feature.Id));

    private static HashSet<string> Closure(FeatureManifest manifest, Feature feature)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>(feature.Requires);
        while (stack.TryPop(out var id))
        {
            if (!seen.Add(id)) continue;
            foreach (var next in manifest.Features.Single(f => f.Id == id).Requires) stack.Push(next);
        }
        return seen;
    }

    private static JsonObject Parameter(FeatureManifest manifest, Feature feature)
    {
        var description = new StringBuilder($"{feature.Description!.TrimEnd('.')} ({feature.PackageId}).");
        if (feature.Store is not null) description.Append($" Needs {feature.Store}.");
        if (feature.NeedsClient) description.Append(" Needs the Angular client.");
        if (feature.Requires.Count > 0)
            description.Append($" Turns on {string.Join(", ", feature.Requires.Select(id => Name(manifest, id)))}.");

        var parameter = new JsonObject
        {
            ["type"] = "parameter",
            ["datatype"] = "bool",
            ["defaultValue"] = feature.Default ? "true" : "false",
            ["displayName"] = feature.DisplayName,
            ["description"] = description.ToString(),
        };
        var enabled = new List<string>();
        if (feature.Store is not null) enabled.Add($"Store == \"{feature.Store}\"");
        if (feature.NeedsClient) enabled.Add("NoClient == false");
        if (enabled.Count > 0) parameter["isEnabled"] = string.Join(" && ", enabled);
        return parameter;
    }

    private static string Name(FeatureManifest manifest, string id)
    {
        var feature = manifest.Features.Single(f => f.Id == id);
        return feature.DisplayName ?? feature.Id;
    }

    private static string PackageReferences(FeatureManifest manifest, string csproj)
    {
        var newline = csproj.Contains("\r\n") ? "\r\n" : "\n";
        var lines = csproj.Split(newline).ToList();
        var markers = lines.Select((l, i) => (l, i)).Where(x => x.l.Trim() == PackageReferencesMarker).ToList();
        if (markers.Count != 1)
            throw new InvalidOperationException($"The host csproj must hold the line '{PackageReferencesMarker}' exactly once; found {markers.Count}.");

        var indent = markers[0].l[..(markers[0].l.Length - markers[0].l.TrimStart().Length)];
        var generated = manifest.Features.SelectMany(f => new[]
        {
            $"<!--#if ({f.Condition}) -->",
            $"{indent}<PackageReference Include=\"{f.PackageId}\" />",
            "<!--#endif -->",
        });
        lines.RemoveAt(markers[0].i);
        lines.InsertRange(markers[0].i, generated);
        return string.Join(newline, lines);
    }

    /// <summary>Refuses a manifest the template engine would turn into a wrong or ambiguous template.</summary>
    public static void Validate(FeatureManifest manifest, JsonObject handSymbols)
    {
        var errors = new List<string>();
        var ids = manifest.Features.Select(f => f.Id).ToList();
        foreach (var duplicate in ids.GroupBy(i => i).Where(g => g.Count() > 1))
            errors.Add($"Feature '{duplicate.Key}' is listed {duplicate.Count()} times.");

        foreach (var feature in manifest.Features)
        {
            if (feature.Store is not (null or "RavenDB" or "InMemory"))
                errors.Add($"{feature.Id}: store '{feature.Store}' is not RavenDB or InMemory.");
            foreach (var required in feature.Requires.Where(r => !ids.Contains(r)))
                errors.Add($"{feature.Id} requires '{required}', which is not a feature.");

            if (feature.IsHandWritten)
            {
                if (!handSymbols.ContainsKey(feature.Symbol!))
                    errors.Add($"{feature.Id}: its symbol '{feature.Symbol}' is not in the hand-written template.json.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(feature.Flag) || string.IsNullOrWhiteSpace(feature.DisplayName) || string.IsNullOrWhiteSpace(feature.Description))
                    errors.Add($"{feature.Id}: a generated feature needs flag, displayName and description.");
                foreach (var name in new[] { feature.Id, feature.Condition }.Where(handSymbols.ContainsKey))
                    errors.Add($"{feature.Id}: the hand-written template.json already defines '{name}', which the generator owns.");
            }
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        // A cycle would make Use{X} undefined; a prerequisite with a constraint its dependent lacks would be switched
        // on where it cannot work (a RavenDB-only prerequisite of an any-store feature).
        foreach (var feature in manifest.Features)
        {
            if (Closure(manifest, feature).Contains(feature.Id))
                errors.Add($"{feature.Id} requires itself through its prerequisites.");
            foreach (var required in feature.Requires.Select(r => manifest.Features.Single(f => f.Id == r)))
            {
                if (required.Store is not null && required.Store != feature.Store)
                    errors.Add($"{feature.Id} requires {required.Id}, which needs {required.Store}; {feature.Id} must declare that store too.");
                if (required.NeedsClient && !feature.NeedsClient)
                    errors.Add($"{feature.Id} requires {required.Id}, which needs the client; {feature.Id} must declare needsClient too.");
            }
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }
}
