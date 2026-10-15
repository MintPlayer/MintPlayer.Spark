using System.Text;
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
/// ⚠️ Only what this installation can accept is compared (§0 D76, <see cref="Applies"/>). The App declares
/// repository, organization and account permissions in one list. An installation holds the repository ones, plus
/// the organization ones on an organization; account permissions are granted per user, when someone authorizes the
/// App. Comparing the whole list asked an organization for <c>emails</c>, and a user account for <c>emails</c>,
/// <c>members</c> and <c>organization_projects</c>: a notice that accepting could never clear (live, 2026-10-14).
/// </para>
/// <para>
/// Cached for a minute per installation, failures included: the notice sits on a page that is reloaded
/// freely, and a failure must cost one log line a minute rather than two GitHub calls per view.
/// </para>
/// <para>
/// ⚠️ Two kinds of failure, two levels (§0 D70). A refusal or an outage (<see cref="ApiException"/>,
/// <see cref="HttpRequestException"/>) is GitHub's and passes: a warning. Anything else means this code
/// misread a reply GitHub did give, or could not build the App client, and fails the same way on every
/// view: an error. The page shows no notice either way. The first live run hid exactly such a defect,
/// an empty-body parse failure, as one warning a minute, and the notice never showed.
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

            var htmlUrl = StringOf(installation.RootElement, "html_url");
            result = htmlUrl is null
                ? null
                : new PendingInstallationPermissions(htmlUrl, ComputePending(
                    ReadPermissions(app.RootElement), ReadPermissions(installation.RootElement),
                    StringOf(installation.RootElement, "target_type")));
        }
        catch (NotFoundException)
        {
            // The installation is gone (uninstalled, and the webhook not processed yet): nothing to accept.
            result = null;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            logger.LogWarning(ex, "Could not read the pending permissions of GitHub installation {InstallationId}", installationId);
            result = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not read the pending permissions of GitHub installation {InstallationId}. This is no GitHub outage, so it repeats on every view until it is fixed", installationId);
            result = null;
        }

        memoryCache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    private static async Task<JsonDocument> GetJsonAsync(IGitHubClient client, string path)
    {
        var response = await client.Connection.GetRaw(new Uri(path, UriKind.Relative), null);
        var body = BodyOf(response);
        if (body.IsEmpty)
            throw new InvalidDataException(
                $"GitHub answered GET /{path} with {(int)response.HttpResponse.StatusCode} and no body to read (content type {response.HttpResponse.ContentType ?? "none"}).");
        return JsonDocument.Parse(body);
    }

    /// <summary>A string member of a reply, or null. A reply that is no JSON object throws: GitHub's never is.</summary>
    private static string? StringOf(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// The bytes of a <see cref="IConnection.GetRaw(Uri, IDictionary{string, string})"/> reply, wherever Octokit's transport
    /// put them (§0 D70). ⚠️ <c>HttpClientAdapter</c> reads a JSON content type as text into <c>HttpResponse.Body</c>, a
    /// <see cref="string"/>, and <c>GetRaw</c>'s typed body is then <c>response.Body as byte[]</c>: null, for every JSON
    /// reply. Only a binary content type (GitHub's raw media type, octet-stream, an image) fills the typed body, from a
    /// stream. Reading the typed body alone parsed nothing, every time.
    /// </summary>
    internal static ReadOnlyMemory<byte> BodyOf(IApiResponse<byte[]> response) => response.HttpResponse?.Body switch
    {
        string text => Encoding.UTF8.GetBytes(text),
        byte[] bytes => bytes,
        _ => response.Body ?? [],
    };

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
    /// Every permission the installation can accept (<see cref="Applies"/>) whose declared level exceeds the
    /// accepted one, ordered by name. Levels rank none &lt; read &lt; write &lt; admin. A level this code does not
    /// know is pending whenever the two sides differ: an unrecognised word must not hide an upgrade.
    /// </summary>
    /// <param name="targetType">The installation's <c>target_type</c>: <c>Organization</c> or <c>User</c>.</param>
    public static IReadOnlyList<PendingPermission> ComputePending(
        IReadOnlyDictionary<string, string> declared, IReadOnlyDictionary<string, string> accepted, string? targetType)
    {
        var pending = new List<PendingPermission>();
        foreach (var (name, declaredLevel) in declared)
        {
            var acceptedLevel = accepted.TryGetValue(name, out var level) ? level : null;
            if (!Applies(name, held: acceptedLevel is not null, targetType))
                continue;

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

    /// <summary>
    /// Whether an installation on a <paramref name="targetType"/> account can accept <paramref name="name"/> (§0 D76).
    /// GitHub's "Choosing permissions for a GitHub App" (read 2026-10-14): organization permissions apply "if it is
    /// installed on an organization account", account permissions "if the user has also authorized your app", and an
    /// installer grants "the repository and organization permissions that the app requested".
    /// <list type="bullet">
    /// <item>An account permission: never. Nothing an installation accepts grants one.</item>
    /// <item>A permission the installation already holds: always. GitHub's own reply shows it applies here.</item>
    /// <item>A repository permission: always. Both kinds of account own repositories.</item>
    /// <item>An enterprise permission: never. Every one starts with <c>enterprise_</c> (15 of 15), and CodeCoverage's
    /// accounts are organizations and users.</item>
    /// <item>Anything else, an organization permission or a name these tables do not know yet: on an organization only.
    /// An organization holds both kinds, so a new repository permission still shows there. On a user account the name
    /// may be organization-only, as <c>members</c>, <c>issue_types</c> and <c>secret_scanning_dismissal_requests</c>
    /// are without an <c>organization_</c> prefix, and it could then never be accepted. The price: a new account
    /// permission missing from <see cref="AccountPermissions"/> shows on organizations until it is added.</item>
    /// </list>
    /// A target type other than <c>Organization</c> counts as a user account, as the installation webhook does.
    /// </summary>
    private static bool Applies(string name, bool held, string? targetType)
    {
        if (AccountPermissions.Contains(name))
            return false;
        if (held || RepositoryPermissions.Contains(name))
            return true;
        if (name.StartsWith("enterprise_", StringComparison.OrdinalIgnoreCase))
            return false;
        return targetType == "Organization";
    }

    // The tables (§0 D76), read 2026-10-14. Categories come from the data behind GitHub's "Permissions required for
    // GitHub Apps" (docs.github.com/en/rest/authentication/permissions-required-for-github-apps): github/docs
    // src/github-apps/data/{fpt,ghec}-2026-03-10/server-to-server-permissions.json at b0207719, where every name's
    // displayTitle reads "Repository", "Organization", "User" or "Enterprise permissions for …". That data covers only
    // permissions some REST endpoint needs, so GitHub's REST description adds discussions, merge_queues, packages,
    // repository_projects and single_file (github/rest-api-description, components/schemas/app-permissions at
    // 33066b02). The schema lists repository, organization, user and enterprise permissions in four blocks, and the 47
    // names both sources share agree on theirs. Its email_addresses and git_ssh_keys are the token request's spelling;
    // GitHub's App payloads say emails and keys, so both are listed.

    /// <summary>Repository permissions: an installation on either kind of account can hold them.</summary>
    private static readonly HashSet<string> RepositoryPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "actions", "actions_variables", "administration", "agent_secrets", "agent_variables", "artifact_metadata",
        "attestations", "checks", "code_quality", "codespaces", "codespaces_lifecycle_admin", "codespaces_metadata",
        "codespaces_secrets", "contents", "copilot_agent_settings", "dependabot_secrets", "deployments", "discussions",
        "environments", "installation_repositories", "issues", "merge_queues", "metadata", "packages", "pages",
        "pull_requests", "repo_secret_scanning_dismissal_requests", "repository_advisories", "repository_creation",
        "repository_custom_properties", "repository_hooks", "repository_projects", "secret_scanning_alerts",
        "secret_scanning_bypass_requests", "secrets", "security_events", "single_file", "statuses",
        "vulnerability_alerts", "workflows",
    };

    /// <summary>Account ("User") permissions: granted per user when someone authorizes the App, never to an installation.</summary>
    private static readonly HashSet<string> AccountPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "blocking", "codespaces_user_secrets", "email_addresses", "emails", "followers", "gists",
        "git_signing_ssh_public_keys", "git_ssh_keys", "gpg_keys", "interaction_limits", "keys", "plan",
        "private_repository_invitations", "profile", "starring", "watching",
    };

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
