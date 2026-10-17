using CodeCoverage.Forge;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeCoverage.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Services;

[Register(typeof(IGitHubAccessService), ServiceLifetime.Scoped)]
public partial class GitHubAccessService : IGitHubAccessService
{
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly UserManager<SparkUser> userManager;
    [Inject] private readonly IGitHubUserTokenService tokenService;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly IMemoryCache memoryCache;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<GitHubAccessService> logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a *failed* lookup is remembered. Deliberately far shorter than
    /// <see cref="CacheDuration"/>: long enough to collapse a burst of requests during an outage,
    /// short enough that recovery is noticed almost immediately.
    /// </summary>
    /// <remarks>
    /// Not caching failures at all — the original behaviour — is defensible with a single provider
    /// and becomes dangerous with several: an outage then means every request re-attempts every
    /// unreachable forge. Bitbucket's budget is roughly 1,000 requests per hour per token, low
    /// enough that the retries alone can exhaust it and then hold it exhausted, turning a brief
    /// outage into a sustained one. The degraded *answer* is still never promoted to a successful
    /// one; only the knowledge that the call failed is held briefly.
    /// </remarks>
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(30);

    public async Task<string[]> GetAllowedOwnersAsync(CancellationToken cancellationToken = default)
        => (await GetVisibilityAsync(cancellationToken)).Owners;

    public async Task<GitHubVisibility> GetVisibilityAsync(CancellationToken cancellationToken = default)
    {
        // The viewer as Spark identifies every caller: the cookie's user id, or the local user an agent token names on
        // /mcp (plan M6). The principal's NameIdentifier alone missed every agent, whose subject is `sub`.
        var user = await ViewerAsync();
        if (user is null)
            return new([], GitHubTokenState.Ok);

        // The "github-" prefix is what keeps this per-(user, provider): a second forge's service
        // uses its own prefix, so two providers' answers can never collide in one entry.
        var cacheKey = $"github-owners/{user.Id}";
        if (memoryCache.TryGetValue<string[]>(cacheKey, out var cached) && cached is not null)
            return new(cached, GitHubTokenState.Ok);

        // ⚠️ The viewer's GitHub login comes from GitHub, for the viewer's own token (GET /user) —
        // never from the principal. ClaimTypes.Name is the Identity user name, which the profile
        // page lets the user edit: reading it here let anyone rename themselves to an organisation's
        // login and be granted that owner, and let the backfill below clear another account's
        // installation id. On the degraded paths, where GitHub cannot be asked, only a login GitHub
        // returned earlier for this user is used (VerifiedIdentityDuration), and otherwise none.
        var identityKey = $"github-identity/{user.Id}";
        var verified = memoryCache.Get<GitHubUser>(identityKey);

        // A recent failure short-circuits, so an outage costs one call per user per
        // FailureCacheDuration rather than one per request. The degraded answer is recomputed
        // rather than stored, so nothing stale is ever served as authoritative.
        var failureKey = $"github-owners-failed/{user.Id}";
        if (memoryCache.TryGetValue<GitHubTokenState>(failureKey, out var failedState))
            return Degraded(verified?.Login, failedState);

        var token = await tokenService.GetAccessTokenAsync(user, forceRefresh: false, cancellationToken);
        if (token.State != GitHubTokenState.Ok)
            return DegradedAndRemember(verified?.Login, token.State, failureKey);

        var answer = await QueryGitHubAsync(token.AccessToken!, user.Id, cancellationToken);
        if (answer.Unauthorized)
        {
            // The token looked fresh but GitHub refused it (revoked, or expiry
            // drifted): force exactly one refresh and retry — the same pattern
            // Spark's installation-token TokenRefreshingHandler uses.
            token = await tokenService.GetAccessTokenAsync(user, forceRefresh: true, cancellationToken);
            if (token.State != GitHubTokenState.Ok)
                return DegradedAndRemember(verified?.Login, token.State, failureKey);

            answer = await QueryGitHubAsync(token.AccessToken!, user.Id, cancellationToken);
            if (answer.Unauthorized)
            {
                // A just-refreshed token GitHub still refuses: the
                // authorization itself is gone. Only a browser fixes this.
                logger.LogWarning("GitHub refused a freshly refreshed token for user {UserId} — reauth required", user.Id);
                return DegradedAndRemember(verified?.Login, GitHubTokenState.ReauthRequired, failureKey);
            }
        }

        if (answer.Me is not { } me || answer.Installations is not { } installations)
        {
            // GitHub unreachable: visibility degrades to the user's own repos for this request.
            // The unknown answer is never promoted to the success cache nor used to clear
            // anything — failure is not absence. Only the fact of the failure is remembered, and
            // only briefly, to stop an outage turning into a retry storm.
            return DegradedAndRemember(answer.Me?.Login ?? verified?.Login, GitHubTokenState.Unavailable, failureKey);
        }

        memoryCache.Set(identityKey, me, VerifiedIdentityDuration);

        await BackfillInstallationIdsAsync(installations, me, cancellationToken);

        var owners = BuildOwnerSet(installations, me.Login);

        memoryCache.Set(cacheKey, owners, CacheDuration);
        return new(owners, GitHubTokenState.Ok);
    }

    /// <summary>
    /// How long a login GitHub returned for the viewer's token stands in for it on the degraded
    /// paths. Bounded, because a GitHub login can be renamed and then claimed by someone else.
    /// </summary>
    private static readonly TimeSpan VerifiedIdentityDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// The owner logins a viewer may manage: every <em>active</em> installation they can reach, plus
    /// their own login.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <b>suspended installation confers nothing.</b> GitHub keeps returning it from
    /// <c>/user/installations</c> so a UI can offer to un-suspend it, but every token minted for it
    /// is refused — so counting it as a grant hands out management rights over an owner we can no
    /// longer act for. <see cref="BackfillInstallationIdsAsync"/> already filtered the same array
    /// this way; this projection did not, which meant suspending an installation revoked nothing.
    /// </para>
    /// <para>
    /// The viewer's own login is always included, and deliberately does not depend on an
    /// installation: it is what the degraded paths fall back to, so the two must agree or a GitHub
    /// outage would widen or narrow a viewer's own repositories.
    /// </para>
    /// <para>
    /// Extracted as a pure function so the suspension rule can be tested without standing up a
    /// token service, a cache and a session.
    /// </para>
    /// </remarks>
    internal static string[] BuildOwnerSet(GitHubInstallation[] installations, string? username)
        => installations
            .Where(i => !i.Suspended)
            .Select(i => i.Login)
            .Concat(username is not null ? [username] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static GitHubVisibility Degraded(string? username, GitHubTokenState state)
        => new(username is not null ? [username] : [], state);

    /// <summary>
    /// Degrades, and remembers <em>that the lookup failed</em> for a short window so a sustained
    /// outage costs one attempt per user per <see cref="FailureCacheDuration"/> instead of one per
    /// request.
    /// </summary>
    /// <remarks>
    /// Only the failure state is stored, never the degraded owner set — the set is rebuilt from the
    /// last login GitHub returned for this user's token each time, so nothing stale is ever served as
    /// though it were authoritative.
    /// The window is not extended on a cache hit either: the short-circuit path calls
    /// <see cref="Degraded"/> directly, so an outage expires on schedule rather than sliding
    /// forward for as long as traffic keeps arriving.
    /// </remarks>
    private GitHubVisibility DegradedAndRemember(string? username, GitHubTokenState state, string failureKey)
    {
        memoryCache.Set(failureKey, state, FailureCacheDuration);
        return Degraded(username, state);
    }

    public async Task<bool> IsOwnerAllowedAsync(string ownerLogin, CancellationToken cancellationToken = default)
    {
        var owners = await GetAllowedOwnersAsync(cancellationToken);
        return owners.Contains(ownerLogin, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Reach first.</b> An owner outside the viewer's owner set (no installation they can reach) is never one this app
    /// manages for them, so it costs no call. Their own login is managed without asking (a personal account is managed by
    /// that user only); any other login is asked of GitHub with the viewer's own token. A personal account that is not the
    /// viewer's answers 404 there, which is "not an owner".
    /// </para>
    /// <para>
    /// <b>Cached per (user, org)</b> with the owner set's lifetime (<see cref="CacheDuration"/>) and its invalidation (the
    /// manual resync drops it, <see cref="GitHubViewerCache"/>). A definite answer (200, 403, 404) is cached for the full
    /// lifetime; a failure (an unreachable GitHub, a rate limit, a refused token) is "no" and remembered only for
    /// <see cref="FailureCacheDuration"/>, like the owner set's. It needs the App's <b>Members: read</b> organization
    /// permission, which the App already declares; an organization that has not granted it answers 403, and nobody
    /// manages it until it does (fail closed).
    /// </para>
    /// </remarks>
    public async Task<bool> ManagesOwnerAsync(string ownerLogin, CancellationToken cancellationToken = default)
    {
        var user = await ViewerAsync();
        if (user is null || string.IsNullOrEmpty(ownerLogin))
            return false;

        if (!await IsOwnerAllowedAsync(ownerLogin, cancellationToken))
            return false;

        var verified = await GetVerifiedLoginAsync(cancellationToken);
        if (verified.Login is { } me && string.Equals(me, ownerLogin, StringComparison.OrdinalIgnoreCase))
            return true;

        // The login goes into a URL path: only a GitHub login shape is ever asked about.
        if (!GitHubLoginShape().IsMatch(ownerLogin))
            return false;

        var key = $"github-org-owner/{user.Id}/{ownerLogin.ToLowerInvariant()}";
        if (memoryCache.TryGetValue<bool>(key, out var cached))
            return cached;

        var (isOwner, definite) = await QueryOrganizationOwnerAsync(user, ownerLogin, cancellationToken);
        GitHubViewerCache.Set(memoryCache, user.Id!, key, isOwner, definite ? CacheDuration : FailureCacheDuration);
        return isOwner;
    }

    /// <summary>
    /// <c>GET /user/memberships/orgs/{org}</c> with the viewer's own token: whether they are an active owner, and whether
    /// that is GitHub's definite answer (200, or 403/404 that is not a rate limit) or a failure read as "no".
    /// </summary>
    private async Task<(bool IsOwner, bool Definite)> QueryOrganizationOwnerAsync(SparkUser user, string org, CancellationToken cancellationToken)
    {
        var token = await tokenService.GetAccessTokenAsync(user, forceRefresh: false, cancellationToken);
        if (token.State != GitHubTokenState.Ok)
            return (false, false);

        var url = $"https://api.github.com/user/memberships/orgs/{Uri.EscapeDataString(org)}";
        var (status, json, rateLimited) = await GetStatusAsync(url, token.AccessToken!, user.Id, cancellationToken);
        if (status == System.Net.HttpStatusCode.Unauthorized)
        {
            token = await tokenService.GetAccessTokenAsync(user, forceRefresh: true, cancellationToken);
            if (token.State != GitHubTokenState.Ok)
                return (false, false);
            (status, json, rateLimited) = await GetStatusAsync(url, token.AccessToken!, user.Id, cancellationToken);
        }

        return status switch
        {
            System.Net.HttpStatusCode.OK when json is not null => (IsActiveOwnerMembership(json), true),
            System.Net.HttpStatusCode.NotFound => (false, true),
            System.Net.HttpStatusCode.Forbidden when !rateLimited => (false, true),
            _ => (false, false),
        };
    }

    /// <summary>
    /// Whether a <c>GET /user/memberships/orgs/{org}</c> body is an owner's: <c>role == "admin"</c> AND
    /// <c>state == "active"</c>. A <c>member</c>, a <c>billing_manager</c> and a pending invitation are not; neither is an
    /// unreadable body.
    /// </summary>
    internal static bool IsActiveOwnerMembership(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String && role.GetString() == "admin"
                && root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "active";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<(System.Net.HttpStatusCode? Status, string? Json, bool RateLimited)> GetStatusAsync(
        string url, string accessToken, string? userId, CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Coverage", "1.0"));

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var rateLimited = response.Headers.RetryAfter is not null
                || (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0");
            if (!response.IsSuccessStatusCode && response.StatusCode is not (System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized))
                logger.LogWarning("GitHub {Url} query failed: {StatusCode} for user {UserId}", url, response.StatusCode, userId);
            var body = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancellationToken) : null;
            return (response.StatusCode, body, rateLimited);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to query GitHub {Url} for user {UserId}", url, userId);
            return (null, null, false);
        }
    }

    /// <summary>A GitHub user or organization login: letters, digits and hyphens, at most 39.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial System.Text.RegularExpressions.Regex GitHubLoginShape();

    public async Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        var user = await ViewerAsync();
        if (user is null)
            return;

        memoryCache.Remove($"github-owners/{user.Id}");

        // The per-(user, org) owner checks and per-(user, account) admin sets go with it (D-O12): a resync that left them
        // cached would keep an organization owner who was just demoted managing it.
        GitHubViewerCache.Invalidate(memoryCache, user.Id!);

        // The failure memo has to go too. A user who has just fixed their authorization and
        // pressed Resync would otherwise keep getting the remembered failure for up to
        // FailureCacheDuration, which reads as "the fix did not work".
        memoryCache.Remove($"github-owners-failed/{user.Id}");
    }

    public async Task<GitHubVerifiedLogin> GetVerifiedLoginAsync(CancellationToken cancellationToken = default)
    {
        var user = await ViewerAsync();
        if (user is null || !user.Logins.Any(l => ForgeProviders.TryParse(l.LoginProvider, out var forge) && forge == EForgeProvider.GitHub))
            return new(null, false, GitHubTokenState.Ok);

        // The same verified identity the owner set is built from (GetVisibilityAsync), so the login the queue acts as is
        // exactly the one this user's own token answers GET /user with.
        var identityKey = $"github-identity/{user.Id}";
        if (memoryCache.Get<GitHubUser>(identityKey) is { } verified)
            return new(verified.Login, true, GitHubTokenState.Ok);

        // Nothing verified in the last VerifiedIdentityDuration: let the visibility query ask GitHub, which remembers the
        // login it returns. Its owner cache goes first, since a cached owner set answers without asking.
        memoryCache.Remove($"github-owners/{user.Id}");
        var visibility = await GetVisibilityAsync(cancellationToken);
        return memoryCache.Get<GitHubUser>(identityKey) is { } asked
            ? new(asked.Login, true, GitHubTokenState.Ok)
            : new(null, true, visibility.TokenState == GitHubTokenState.Ok ? GitHubTokenState.Unavailable : visibility.TokenState);
    }

    /// <summary>The signed-in viewer's user document; <see langword="null"/> for an anonymous caller.</summary>
    private async Task<SparkUser?> ViewerAsync()
        => currentUser.Id is { Length: > 0 } userId ? await userManager.FindByIdAsync(userId) : null;

    /// <summary>
    /// Who the token belongs to (GET /user) and what it can reach (GET /user/installations), both
    /// asked of GitHub with the same token. A null half means "don't know" (that request failed),
    /// which callers must treat differently from an empty but successful response. Unauthorized —
    /// from either request — is surfaced separately so the caller can refresh and retry.
    /// </summary>
    private async Task<(GitHubUser? Me, GitHubInstallation[]? Installations, bool Unauthorized)> QueryGitHubAsync(
        string accessToken, string? userId, CancellationToken cancellationToken)
    {
        var (meJson, meUnauthorized) = await GetJsonAsync("https://api.github.com/user", accessToken, userId, cancellationToken);
        if (meUnauthorized)
            return (null, null, true);
        var me = meJson is null ? null : ParseUser(meJson);

        var (installationsJson, installationsUnauthorized) =
            await GetJsonAsync("https://api.github.com/user/installations", accessToken, userId, cancellationToken);
        if (installationsUnauthorized)
            return (me, null, true);

        return (me, installationsJson is null ? null : ParseInstallations(installationsJson), false);
    }

    private async Task<(string? Json, bool Unauthorized)> GetJsonAsync(
        string url, string accessToken, string? userId, CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Coverage", "1.0"));

            var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return (null, Unauthorized: true);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("GitHub {Url} query failed: {StatusCode} for user {UserId}", url, response.StatusCode, userId);
                return (null, false);
            }

            return (await response.Content.ReadAsStringAsync(cancellationToken), false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to query GitHub {Url} for user {UserId}", url, userId);
            return (null, false);
        }
    }

    /// <summary>The token owner from GET /user, or null when the body lacks a numeric id or a login.</summary>
    public static GitHubUser? ParseUser(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // TryGetInt64 throws rather than returning false on a non-number, so check the kind first.
            if (!root.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.Number
                || !idNode.TryGetInt64(out var id)) return null;
            if (!root.TryGetProperty("login", out var loginNode) || loginNode.ValueKind != JsonValueKind.String) return null;
            var login = loginNode.GetString();
            return string.IsNullOrEmpty(login) ? null : new GitHubUser(id, login);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static GitHubInstallation[] ParseInstallations(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("installations", out var installations))
            return [];

        var result = new List<GitHubInstallation>();
        foreach (var installation in installations.EnumerateArray())
        {
            if (!installation.TryGetProperty("id", out var idNode) || !idNode.TryGetInt64(out var installationId)) continue;
            if (!installation.TryGetProperty("account", out var account)) continue;
            if (account.ValueKind != JsonValueKind.Object) continue;
            if (!account.TryGetProperty("id", out var accountIdNode) || !accountIdNode.TryGetInt64(out var accountId)) continue;
            if (!account.TryGetProperty("login", out var loginNode)) continue;
            var login = loginNode.GetString();
            if (string.IsNullOrEmpty(login)) continue;

            var type = installation.TryGetProperty("target_type", out var typeNode) ? typeNode.GetString() : null;
            var avatarUrl = account.TryGetProperty("avatar_url", out var avatarNode) ? avatarNode.GetString() : null;
            var suspended = installation.TryGetProperty("suspended_at", out var suspendedNode)
                && suspendedNode.ValueKind != JsonValueKind.Null;

            result.Add(new GitHubInstallation(installationId, accountId, login, type, avatarUrl, suspended));
        }
        return [.. result];
    }

    /// <summary>
    /// GET /user/installations already carries the current installation id per
    /// account — persist it, because the `installation` webhook is the only
    /// other writer and GitHub never redelivers a lost one (which left the
    /// "App installed" badge permanently grey). Sets/corrects ids for every
    /// account in the response; clears only the signed-in user's OWN account
    /// when it is absent — users always see their own installation, so that
    /// absence is authoritative (a missed uninstall webhook otherwise leaves
    /// the badge green forever). For orgs, absence may simply mean lost
    /// visibility, so clearing those stays the webhook's job.
    /// </summary>
    private async Task BackfillInstallationIdsAsync(GitHubInstallation[] installations, GitHubUser me, CancellationToken cancellationToken)
    {
        var active = installations.Where(i => !i.Suspended).ToArray();

        try
        {
            var loaded = await session.LoadAsync<Account>(
                active.Select(i => Account.DocumentId(EForgeProvider.GitHub, i.AccountGitHubId)), cancellationToken);

            foreach (var installation in active)
            {
                var id = Account.DocumentId(EForgeProvider.GitHub, installation.AccountGitHubId);
                var account = loaded.GetValueOrDefault(id);
                if (account is null)
                {
                    account = new Account
                    {
                        GitHubId = installation.AccountGitHubId,
                        Login = installation.Login,
                        Type = installation.Type == "Organization" ? "Organization" : "User",
                        AvatarUrl = installation.AvatarUrl,
                    };
                    await session.StoreAsync(account, id, cancellationToken);
                }
                account.InstallationId = installation.Id;
            }

            // The own account by GitHub's numeric id, not by login: ids are what GitHub attested for
            // this token, and they survive a login rename that the Account document may not have
            // caught up with yet.
            if (!active.Any(i => i.AccountGitHubId == me.Id))
            {
                var own = await session.LoadAsync<Account>(Account.DocumentId(EForgeProvider.GitHub, me.Id), cancellationToken);
                if (own?.InstallationId is not null)
                {
                    own.InstallationId = null;
                    logger.LogInformation(
                        "Cleared InstallationId for {Login}: own account absent from /user/installations", me.Login);
                }
            }

            if (session.Advanced.HasChanges)
            {
                // The caller immediately queries Accounts by Login; wait for
                // indexing so a just-created account shows up in that query
                // (existing accounts are unaffected — their index entry is
                // already there and documents load fresh).
                session.Advanced.WaitForIndexesAfterSaveChanges(TimeSpan.FromSeconds(5), throwOnTimeout: false);
                await session.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Visibility must never depend on the backfill.
            logger.LogError(ex, "Failed to backfill installation ids");
        }
    }
}

/// <summary>The owner of a user token, from GET /user: GitHub's numeric id and current login.</summary>
public sealed record GitHubUser(long Id, string Login);

/// <summary>One entry of GET /user/installations, reduced to what we consume.</summary>
public sealed record GitHubInstallation(long Id, long AccountGitHubId, string Login, string? Type, string? AvatarUrl, bool Suspended);
