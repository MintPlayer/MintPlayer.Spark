using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Templates.Build;

/// <summary>
/// The spark-app template's <c>features.json</c> (docs/templates_dnx_terminal_PRD.md §4.2, D15). Project paths are
/// relative to <c>libs/</c>, and a project's file name is its package id (true for every Spark package).
/// </summary>
public sealed class FeatureManifest
{
    public List<ManifestProject> Core { get; init; } = [];
    public List<Feature> Features { get; init; } = [];
    public List<ExcludedProject> Excluded { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static FeatureManifest Load(string path)
        => JsonSerializer.Deserialize<FeatureManifest>(File.ReadAllText(path), Options)
           ?? throw new InvalidOperationException($"{path} is empty.");
}

public class ManifestProject
{
    public string Project { get; init; } = "";

    /// <summary>The package id: the project's file name.</summary>
    [JsonIgnore]
    public string PackageId => Path.GetFileNameWithoutExtension(Project);
}

public sealed class ExcludedProject : ManifestProject
{
    public string Reason { get; init; } = "";
}

public sealed class Feature : ManifestProject
{
    /// <summary>The template parameter's name, e.g. <c>History</c>; the generated condition is <c>Use{Id}</c>.</summary>
    public string Id { get; init; } = "";

    /// <summary>The name of the generated condition, when it is not <c>Use{Id}</c> (Authorization: <c>HasAuth</c>).</summary>
    public string? Symbol { get; init; }

    /// <summary>
    /// Set when a hand-written parameter chooses this feature instead of a generated bool, e.g. Authorization's
    /// <c>(AuthMode == "authorization" || AuthMode == "oidc")</c>. The generator then emits no parameter, but still
    /// emits the condition, so a feature that requires this one switches it on (D16).
    /// </summary>
    public string? SelectedWhen { get; init; }

    /// <summary>The CLI flag, without dashes.</summary>
    public string? Flag { get; init; }

    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Default { get; init; }

    /// <summary><c>RavenDB</c> or <c>InMemory</c> when the feature works with one store only.</summary>
    public string? Store { get; init; }

    /// <summary>The feature needs the Angular client (not generated with <c>--no-client</c>).</summary>
    public bool NeedsClient { get; init; }

    /// <summary>Other features this one switches on; equal to the features among its ProjectReferences.</summary>
    public List<string> Requires { get; init; } = [];

    /// <summary>
    /// Features the app must register for this one to work, although its project does not reference their package
    /// (Replication references only Messaging.Abstractions, yet needs <c>AddMessaging()</c>). Each entry is a fact of
    /// the library's runtime, kept apart from <see cref="Requires"/> so the graph test stays exact.
    /// </summary>
    public List<string> AlsoRequires { get; init; } = [];

    /// <summary><see cref="Requires"/> and <see cref="AlsoRequires"/>: every feature this one switches on.</summary>
    [JsonIgnore]
    public IEnumerable<string> AllRequires => Requires.Concat(AlsoRequires);

    /// <summary>The <c>spark.X(</c> call Program.cs makes for it.</summary>
    public string? Registration { get; init; }

    /// <summary>The ng-spark providers app.config.ts calls for it.</summary>
    public List<string> Client { get; init; } = [];

    /// <summary>The appsettings.json section it reads, e.g. <c>Spark:Mail</c>.</summary>
    public string? Appsettings { get; init; }

    /// <summary>The feature is a generated bool parameter (not chosen through <see cref="SelectedWhen"/>).</summary>
    [JsonIgnore]
    public bool HasParameter => SelectedWhen is null;

    /// <summary>The expression that is true when the user chose this feature itself.</summary>
    [JsonIgnore]
    public string Selected => SelectedWhen is null ? $"({Id} && true)" : $"({SelectedWhen})";

    /// <summary>The template condition that is true when the generated app has this feature.</summary>
    [JsonIgnore]
    public string Condition => Symbol ?? $"Use{Id}";
}
