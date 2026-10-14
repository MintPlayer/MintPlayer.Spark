using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Microsoft.Extensions.Caching.Memory;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;
using Account = CodeCoverage.Entities.Account;

namespace CodeCoverage.Services;

/// <summary>
/// The "accept the new permissions" notice (merge-queue PRD O17). When the App's permissions are raised
/// (the merge queue adds <c>actions</c> and <c>workflows</c>), every installation keeps its old grant
/// until someone on the account accepts the upgrade on GitHub. This compares what the App declares
/// (<c>GET /app</c>) with what the installation accepted (<c>GET /app/installations/{id}</c>) and hands
/// back the installation's own <c>html_url</c>, so the link is never hard-coded or built from a login.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ Compared as raw JSON, not through Octokit's <see cref="InstallationPermissions"/>. Octokit 14 types 36
/// permissions; GitHub has more (<c>merge_queues</c>, <c>attestations</c>, …), and a permission the typed
/// object does not know would be silently missing from both sides, so its pending upgrade would never show.
/// </para>
/// <para>
/// Cached for a minute per installation, failures included: the notice sits on a page that is reloaded
/// freely, and an outage must cost one logged warning a minute rather than two GitHub calls per view.
/// </para>
/// </remarks>
[Register(typeof(IForgeInstallationPermissions), ServiceLifetime.Scoped)]
public partial class GitHubInstallationPermissions : IForgeInstallationPermissions
{
    [Inject] private readonly IGitHubInstallationService installationService;
    [Inject] private readonly IMemoryCache memoryCache;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly IWebHostEnvironment environment;
    [Inject] private readonly ILogger<GitHubInstallationPermissions> logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    public EForgeProvider Provider => EForgeProvider.GitHub;

    public async Task<PendingInstallationPermissions?> GetPendingAsync(Account account, CancellationToken cancellationToken = default)
    {
        if (account.Provider != EForgeProvider.GitHub || account.InstallationId is not { } installationId)
            return null;

        // Unconfigured (the normal dev state): say nothing, and log nothing either.
        var envPrefix = environment.EnvironmentName;
        if (string.IsNullOrEmpty(configuration[$"GitHub:{envPrefix}:PrivateKeyPath"])
            || !long.TryParse(configuration[$"GitHub:{envPrefix}:AppId"], out _))
            return null;

        var cacheKey = $"github-installation-permissions:{installationId}";
        if (memoryCache.TryGetValue(cacheKey, out PendingInstallationPermissions? cached))
            return cached;

        PendingInstallationPermissions? result;
        try
        {
            var client = await installationService.CreateAppClientAsync();
            using var app = await GetJsonAsync(client, "app");
            using var installation = await GetJsonAsync(client, $"app/installations/{installationId}");

            var htmlUrl = installation.RootElement.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String
                ? url.GetString()
                : null;
            result = htmlUrl is null
                ? null
                : new PendingInstallationPermissions(htmlUrl, ComputePending(
                    ReadPermissions(app.RootElement), ReadPermissions(installation.RootElement)));
        }
        catch (NotFoundException)
        {
            // The installation is gone (uninstalled, and the webhook not processed yet): nothing to accept.
            result = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the pending permissions of GitHub installation {InstallationId}", installationId);
            result = null;
        }

        memoryCache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    private static async Task<JsonDocument> GetJsonAsync(IGitHubClient client, string path)
    {
        var response = await client.Connection.GetRaw(new Uri(path, UriKind.Relative), null);
        return JsonDocument.Parse(response.Body ?? []);
    }

    /// <summary>The <c>permissions</c> object of an App or installation payload, as name → level.</summary>
    public static IReadOnlyDictionary<string, string> ReadPermissions(JsonElement payload)
    {
        var permissions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("permissions", out var element)
            || element.ValueKind != JsonValueKind.Object)
            return permissions;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } level)
                permissions[property.Name] = level;
        }
        return permissions;
    }

    /// <summary>
    /// Every permission whose declared level exceeds the accepted one, ordered by name. Levels rank
    /// none &lt; read &lt; write &lt; admin. A level this code does not know is pending whenever the two
    /// sides differ: an unrecognised word must not hide an upgrade.
    /// </summary>
    public static IReadOnlyList<PendingPermission> ComputePending(
        IReadOnlyDictionary<string, string> declared, IReadOnlyDictionary<string, string> accepted)
    {
        var pending = new List<PendingPermission>();
        foreach (var (name, declaredLevel) in declared)
        {
            var acceptedLevel = accepted.TryGetValue(name, out var level) ? level : null;
            var declaredRank = Rank(declaredLevel);
            var acceptedRank = acceptedLevel is null ? 0 : Rank(acceptedLevel);

            var isPending = declaredRank is null || acceptedRank is null
                ? !string.Equals(declaredLevel, acceptedLevel, StringComparison.OrdinalIgnoreCase)
                : declaredRank > acceptedRank;
            if (isPending)
                pending.Add(new PendingPermission(name, DisplayName(name), declaredLevel, acceptedLevel));
        }
        return [.. pending.OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    private static int? Rank(string level) => level.ToLowerInvariant() switch
    {
        "none" => 0,
        "read" => 1,
        "write" => 2,
        "admin" => 3,
        _ => null,
    };

    /// <summary><c>pull_requests</c> → <c>Pull requests</c>.</summary>
    public static string DisplayName(string name)
    {
        var words = name.Replace('_', ' ').Trim();
        return words.Length == 0 ? name : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
