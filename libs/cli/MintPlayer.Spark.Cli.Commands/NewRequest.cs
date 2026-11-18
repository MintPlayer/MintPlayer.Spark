using MintPlayer.Spark.Templates.Build;

namespace MintPlayer.Spark.Cli;

/// <summary>
/// What <c>spark new</c> was asked for, whether by options or by the prompts. <see cref="Validate"/> refuses what the
/// template would refuse or silently drop (a feature its store or client choice disables), so the user hears it before
/// anything is written.
/// </summary>
/// <param name="Features">The chosen feature flags; null means the manifest's defaults.</param>
public sealed record NewRequest(
    string Name,
    string Output,
    string Store,
    string Auth,
    string? OidcAuthority,
    IReadOnlyList<string>? Features,
    bool NoClient)
{
    public const string RavenDb = "RavenDB";
    public const string InMemory = "InMemory";
    public static readonly string[] Stores = [RavenDb, InMemory];
    public static readonly string[] AuthModes = ["none", "authorization", "oidc"];

    private const string AuthorizationId = "Authorization";

    public IReadOnlyList<string> Validate(FeatureManifest manifest)
    {
        var errors = new List<string>();
        if (!Stores.Contains(Store)) errors.Add($"--store must be one of {string.Join(", ", Stores)}.");
        if (!AuthModes.Contains(Auth)) errors.Add($"--auth must be one of {string.Join(", ", AuthModes)}.");
        if (Auth == "oidc" && string.IsNullOrWhiteSpace(OidcAuthority))
            errors.Add("--auth oidc needs --oidc-authority, the identity provider's address.");
        if (Auth != "oidc" && !string.IsNullOrWhiteSpace(OidcAuthority))
            errors.Add("--oidc-authority only applies with --auth oidc.");

        var byFlag = manifest.Features.Where(f => f.HasParameter && f.Flag is not null).ToDictionary(f => f.Flag!, StringComparer.OrdinalIgnoreCase);
        foreach (var flag in Features ?? [])
            if (!byFlag.ContainsKey(flag))
                errors.Add($"There is no feature \"{flag}\". The features: {string.Join(", ", byFlag.Keys.Order())}.");

        foreach (var root in Roots(manifest))
        {
            var label = root.Id == AuthorizationId ? $"Sign-in (--auth {Auth})" : $"--{root.Flag}";
            var closure = Closure(manifest, [root]);
            if (closure.FirstOrDefault(f => f.Store is not null && f.Store != Store) is { } storeBound)
                errors.Add($"{label} needs the {storeBound.Store} store{Via(root, storeBound)}; it is not available with --store {Store}.");
            if (NoClient && closure.FirstOrDefault(f => f.NeedsClient) is { } clientBound)
                errors.Add($"{label} needs the Angular client{Via(root, clientBound)}; it is not available with --no-client.");
        }
        return errors;
    }

    /// <summary>The flags of every feature the app gets: the chosen ones and their prerequisites.</summary>
    public IReadOnlyList<string> SelectedFlags(FeatureManifest manifest)
        => Closure(manifest, Roots(manifest)).Where(f => f.Flag is not null).Select(f => f.Flag!).ToList();

    /// <summary>The features the app gets only because a chosen one requires them; <c>spark new</c> lists them.</summary>
    public IReadOnlyList<string> AddedPrerequisites(FeatureManifest manifest)
    {
        var roots = Roots(manifest);
        return Closure(manifest, roots).Except(roots).Select(f => f.Id).ToList();
    }

    /// <summary>
    /// The <c>dotnet new</c> command line. Every feature the store and client choice leave enabled is switched on or off
    /// explicitly, so the result never depends on the template's defaults drifting from the tool's.
    /// </summary>
    public IReadOnlyList<string> TemplateArguments(FeatureManifest manifest)
    {
        var arguments = new List<string> { "new", "spark-app", "--name", Name, "--output", Output, "--store", Store, "--auth", Auth };
        if (!string.IsNullOrWhiteSpace(OidcAuthority)) arguments.AddRange(["--oidc-authority", OidcAuthority]);
        if (NoClient) arguments.AddRange(["--no-client", "true"]);

        var selected = Closure(manifest, Roots(manifest)).ToHashSet();
        foreach (var feature in manifest.Features.Where(f => f.HasParameter && f.Flag is not null))
        {
            if (feature.Store is not null && feature.Store != Store) continue;
            if (feature.NeedsClient && NoClient) continue;
            arguments.AddRange([$"--{feature.Flag}", selected.Contains(feature) ? "true" : "false"]);
        }
        return arguments;
    }

    /// <summary>The features asked for directly: the flags, and Authorization when sign-in is on.</summary>
    private List<Feature> Roots(FeatureManifest manifest)
    {
        var roots = Features is null
            ? manifest.Features.Where(f => f.HasParameter && f.Default).ToList()
            : manifest.Features.Where(f => f.Flag is not null && Features.Contains(f.Flag, StringComparer.OrdinalIgnoreCase)).ToList();
        if (Auth != "none" && manifest.Features.FirstOrDefault(f => f.Id == AuthorizationId) is { } authorization)
            roots.Insert(0, authorization);
        return roots;
    }

    private static List<Feature> Closure(FeatureManifest manifest, IEnumerable<Feature> roots)
    {
        var byId = manifest.Features.ToDictionary(f => f.Id);
        var result = new List<Feature>();
        var pending = new Queue<Feature>(roots);
        while (pending.TryDequeue(out var feature))
        {
            if (result.Contains(feature)) continue;
            result.Add(feature);
            foreach (var required in feature.AllRequires)
                if (byId.TryGetValue(required, out var next)) pending.Enqueue(next);
        }
        return result;
    }

    private static string Via(Feature root, Feature bound) => bound == root ? "" : $" (through {bound.Id})";
}
