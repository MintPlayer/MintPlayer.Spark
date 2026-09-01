using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using CodeCoverage.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Services;

[Register(typeof(IGitHubAccessService), ServiceLifetime.Scoped)]
public partial class GitHubAccessService : IGitHubAccessService
{
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly UserManager<SparkUser> userManager;
    [Inject] private readonly IGitHubUserTokenService tokenService;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly IMemoryCache memoryCache;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<GitHubAccessService> logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public async Task<string[]> GetAllowedOwnersAsync(CancellationToken cancellationToken = default)
        => (await GetVisibilityAsync(cancellationToken)).Owners;

    public async Task<GitHubVisibility> GetVisibilityAsync(CancellationToken cancellationToken = default)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return new([], GitHubTokenState.Ok);

        var user = await userManager.GetUserAsync(principal);
        if (user is null)
            return new([], GitHubTokenState.Ok);

        var cacheKey = $"github-owners/{user.Id}";
        if (memoryCache.TryGetValue<string[]>(cacheKey, out var cached) && cached is not null)
            return new(cached, GitHubTokenState.Ok);

        var username = principal.FindFirstValue(ClaimTypes.Name);

        var token = await tokenService.GetAccessTokenAsync(user, forceRefresh: false, cancellationToken);
        if (token.State != GitHubTokenState.Ok)
            return Degraded(username, token.State);

        var (installations, unauthorized) = await QueryGitHubInstallationsAsync(token.AccessToken!, user.Id, username, cancellationToken);
        if (unauthorized)
        {
            // The token looked fresh but GitHub refused it (revoked, or expiry
            // drifted): force exactly one refresh and retry — the same pattern
            // Spark's installation-token TokenRefreshingHandler uses.
            token = await tokenService.GetAccessTokenAsync(user, forceRefresh: true, cancellationToken);
            if (token.State != GitHubTokenState.Ok)
                return Degraded(username, token.State);

            (installations, unauthorized) = await QueryGitHubInstallationsAsync(token.AccessToken!, user.Id, username, cancellationToken);
            if (unauthorized)
            {
                // A just-refreshed token GitHub still refuses: the
                // authorization itself is gone. Only a browser fixes this.
                logger.LogWarning("GitHub refused a freshly refreshed token for user {UserId} ({Login}) — reauth required", user.Id, username);
                return Degraded(username, GitHubTokenState.ReauthRequired);
            }
        }

        if (installations is null)
        {
            // GitHub unreachable: visibility degrades to the user's own repos
            // for this request, but an unknown answer is neither cached nor
            // used to clear anything — failure is not absence.
            return Degraded(username, GitHubTokenState.Unavailable);
        }

        await BackfillInstallationIdsAsync(installations, username, cancellationToken);

        var owners = installations
            .Select(i => i.Login)
            .Concat(username is not null ? [username] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        memoryCache.Set(cacheKey, owners, CacheDuration);
        return new(owners, GitHubTokenState.Ok);
    }

    private static GitHubVisibility Degraded(string? username, GitHubTokenState state)
        => new(username is not null ? [username] : [], state);

    public async Task<bool> IsOwnerAllowedAsync(string ownerLogin, CancellationToken cancellationToken = default)
    {
        var owners = await GetAllowedOwnersAsync(cancellationToken);
        return owners.Contains(ownerLogin, StringComparer.OrdinalIgnoreCase);
    }

    public async Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return;

        var user = await userManager.GetUserAsync(principal);
        if (user is not null)
            memoryCache.Remove($"github-owners/{user.Id}");
    }

    /// <summary>Null installations means "don't know" (request failed), which
    /// callers must treat differently from an empty but successful response.
    /// Unauthorized is surfaced separately so the caller can refresh and retry.</summary>
    private async Task<(GitHubInstallation[]? Installations, bool Unauthorized)> QueryGitHubInstallationsAsync(
        string accessToken, string? userId, string? username, CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/installations");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Coverage", "1.0"));

            var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return (null, Unauthorized: true);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("GitHub /user/installations query failed: {StatusCode} for user {UserId} ({Login})",
                    response.StatusCode, userId, username);
                return (null, false);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return (ParseInstallations(json), false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to query GitHub installations for user {UserId} ({Login})", userId, username);
            return (null, false);
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
    private async Task BackfillInstallationIdsAsync(GitHubInstallation[] installations, string? username, CancellationToken cancellationToken)
    {
        var active = installations.Where(i => !i.Suspended).ToArray();

        try
        {
            var loaded = await session.LoadAsync<Account>(
                active.Select(i => Account.DocumentId(i.AccountGitHubId)), cancellationToken);

            foreach (var installation in active)
            {
                var id = Account.DocumentId(installation.AccountGitHubId);
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

            if (username is not null
                && !active.Any(i => string.Equals(i.Login, username, StringComparison.OrdinalIgnoreCase)))
            {
                var own = await session.Query<Account, Indexes.Accounts_Overview>()
                    .Where(a => a.Login == username)
                    .FirstOrDefaultAsync(cancellationToken);
                if (own?.InstallationId is not null)
                {
                    own.InstallationId = null;
                    logger.LogInformation(
                        "Cleared InstallationId for {Login}: own account absent from /user/installations", username);
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

/// <summary>One entry of GET /user/installations, reduced to what we consume.</summary>
public sealed record GitHubInstallation(long Id, long AccountGitHubId, string Login, string? Type, string? AvatarUrl, bool Suspended);
