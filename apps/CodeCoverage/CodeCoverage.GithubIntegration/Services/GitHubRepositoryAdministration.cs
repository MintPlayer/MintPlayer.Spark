using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeCoverage.Forge;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Account = CodeCoverage.Entities.Account;
using Repository = CodeCoverage.Entities.Repository;

namespace CodeCoverage.Services;

/// <summary>
/// GitHub's <see cref="IForgeRepositoryAdministration"/> (dependency-updates PRD R21, R32, R33, R35).
/// </summary>
/// <remarks>
/// <para>
/// <b>The listing is the viewer's own token.</b> <c>GET /user/installations</c>, then
/// <c>GET /user/installations/{id}/repositories</c> per installation: GitHub scopes both to installations of this App the
/// user can reach, and the second answers <c>permissions.admin</c> per repository. A forged or foreign
/// <c>installation_id</c> answers 403 or 404, which is read as "nothing here" (no existence oracle). A 401 refreshes the
/// token once, as <see cref="GitHubAccessService"/> does.
/// </para>
/// <para>
/// <b>The execution-time check is the App's installation token</b>, so it does not depend on the viewer's token still
/// being fresh: one aliased GraphQL query per 100 repositories, <c>collaborators(login:, first: 1)</c>'s edge
/// <c>permission</c> (<c>ADMIN</c> only, R33). Whether an installation token may read that connection is not verified live
/// (D-O3): any GraphQL error other than a rate limit falls back, automatically, to REST
/// <c>GET /repos/{o}/{r}/collaborators/{login}/permission</c> per repository (Metadata: read), whose <c>permission</c> is
/// <c>admin</c> only for the admin role (maintain reads <c>write</c>).
/// </para>
/// </remarks>
[Register(typeof(IForgeRepositoryAdministration), ServiceLifetime.Scoped)]
public partial class GitHubRepositoryAdministration : IForgeRepositoryAdministration
{
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly UserManager<SparkUser> userManager;
    [Inject] private readonly IGitHubUserTokenService tokenService;
    [Inject] private readonly IGitHubAccessService access;
    [Inject] private readonly IGitHubInstallationService installationService;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly ILogger<GitHubRepositoryAdministration> logger;
    [Inject] private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache memoryCache;

    private static readonly Uri ApiBase = new("https://api.github.com/");
    private const string ApiVersion = "2022-11-28";

    /// <summary>GitHub's page size cap; also the aliases per GraphQL query (R21).</summary>
    internal const int PageSize = 100;

    /// <summary>A bound on the listing: 30 pages of installations or of one installation's repositories.</summary>
    private const int MaxPages = 30;

    public EForgeProvider Provider => EForgeProvider.GitHub;

    public async Task<ForgeViewerLogin> GetViewerLoginAsync(CancellationToken cancellationToken = default)
    {
        var verified = await access.GetVerifiedLoginAsync(cancellationToken);
        return new ForgeViewerLogin(verified.Login, verified.Linked, State(verified.TokenState));
    }

    public async Task<ForgeViewerRepositories> ListViewerRepositoriesAsync(string? installationKey, CancellationToken cancellationToken = default)
    {
        var login = await GetViewerLoginAsync(cancellationToken);
        if (login.Login is null)
            return ForgeViewerRepositories.None(null, login.State);

        long? only = null;
        if (installationKey is not null)
        {
            // Not a number: not a GitHub installation, and so nothing of the viewer's. The same answer, login included, as
            // a number GitHub answers 403/404 for (D-O13: no existence oracle, and no telling a malformed key apart).
            if (!long.TryParse(installationKey, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                return ForgeViewerRepositories.None(login.Login, EForgeCredentialState.Ok);
            only = parsed;
        }
        if (currentUser.Id is not { Length: > 0 } userId || await userManager.FindByIdAsync(userId) is not { } user)
            return ForgeViewerRepositories.None(null, EForgeCredentialState.Ok);

        var session = new UserSession(this, user);
        var installations = new List<(long Id, long AccountId, string AccountLogin)>();
        if (only is { } id)
        {
            installations.Add((id, 0, string.Empty));
        }
        else
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                var (status, json) = await session.GetAsync($"user/installations?per_page={PageSize}&page={page}", cancellationToken);
                if (status != HttpStatusCode.OK || json is null)
                    return ForgeViewerRepositories.None(login.Login, status == HttpStatusCode.Unauthorized ? EForgeCredentialState.ReauthRequired : EForgeCredentialState.Unavailable);
                using (json)
                {
                    var items = Items(json.RootElement, "installations").ToList();
                    foreach (var item in items)
                        if (Num(item, "id") is { } installationId && !Flag(item, "suspended_at"))
                            installations.Add((installationId, Num(Child(item, "account"), "id") ?? 0, Str(Child(item, "account"), "login") ?? string.Empty));
                    if (items.Count < PageSize) break;
                }
            }
        }

        var rows = new List<ForgeViewerRepository>();
        foreach (var installation in installations)
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                var (status, json) = await session.GetAsync(
                    $"user/installations/{installation.Id.ToString(CultureInfo.InvariantCulture)}/repositories?per_page={PageSize}&page={page}", cancellationToken);
                // 403/404: an installation this viewer cannot reach, or none at all. The same empty answer for both (R20).
                if (status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                    break;
                if (status != HttpStatusCode.OK || json is null)
                    return ForgeViewerRepositories.None(login.Login, status == HttpStatusCode.Unauthorized ? EForgeCredentialState.ReauthRequired : EForgeCredentialState.Unavailable);
                using (json)
                {
                    var items = Items(json.RootElement, "repositories").ToList();
                    rows.AddRange(items.Select(r => Row(installation.Id, r)).OfType<ForgeViewerRepository>());
                    if (items.Count < PageSize) break;
                }
            }
        }
        return new ForgeViewerRepositories(rows, login.Login, EForgeCredentialState.Ok);
    }

    /// <summary>One repository of <c>/user/installations/{id}/repositories</c>; null without its ids.</summary>
    internal static ForgeViewerRepository? Row(long installationId, JsonElement repository)
    {
        var owner = Child(repository, "owner");
        if (Num(repository, "id") is not { } id || Str(repository, "full_name") is not { } fullName || Num(owner, "id") is not { } ownerId)
            return null;
        return new ForgeViewerRepository(installationId.ToString(CultureInfo.InvariantCulture), ownerId, Str(owner, "login") ?? string.Empty, id, fullName,
            Flag(repository, "private"), Flag(Child(repository, "permissions"), "admin"));
    }

    /// <summary>How long a viewer's admin set per account is kept: the owner set's lifetime (<see cref="GitHubAccessService"/>).</summary>
    private static readonly TimeSpan AdministeredCacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>How long a failed admin check is remembered as "none": the owner set's failure memo.</summary>
    private static readonly TimeSpan AdministeredFailureCacheDuration = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlySet<string>> GetAdministeredRepositoriesAsync(Account account, IReadOnlyList<Repository> repositories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (currentUser.Id is not { Length: > 0 } userId || account.Id is null || repositories.Count == 0)
            return EmptySet;

        var key = $"github-repo-admins/{userId}/{account.Id}";
        if (memoryCache.TryGetValue<IReadOnlySet<string>>(key, out var cached) && cached is not null)
            return cached;

        if ((await GetViewerLoginAsync(cancellationToken)).Login is not { } login)
            return EmptySet;

        IReadOnlySet<string> administered;
        var lifetime = AdministeredCacheDuration;
        try
        {
            var answer = await CheckAdministratorAsync(account, repositories, login, cancellationToken);
            administered = answer.Where(a => a.Value).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A rate limit or an unreachable GitHub: nobody administers anything for a short while, never a stale "yes".
            logger.LogWarning(ex, "Repository admin check failed for {Account}; treating the viewer as no admin", account.Login);
            administered = EmptySet;
            lifetime = AdministeredFailureCacheDuration;
        }

        GitHubViewerCache.Set(memoryCache, userId, key, administered, lifetime);
        return administered;
    }

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>(StringComparer.Ordinal);

    public async Task<IReadOnlyDictionary<string, bool>> CheckAdministratorAsync(Account account, IReadOnlyList<Repository> repositories, string login,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrEmpty(login);
        var answer = repositories.Where(r => r.Id is not null).ToDictionary(r => r.Id!, _ => false, StringComparer.Ordinal);
        if (answer.Count == 0 || account.InstallationId is not { } installationId || !GitHubLogin().IsMatch(login))
            return answer;

        var token = await installationService.GetInstallationTokenAsync(installationId, cancellationToken);
        foreach (var chunk in repositories.Where(r => r.Id is not null).Chunk(PageSize))
        {
            IReadOnlyDictionary<string, bool>? graph;
            try
            {
                graph = await CheckWithGraphQLAsync(token, chunk, login, cancellationToken);
            }
            catch (ForgeRequestException ex) when (ForgeRateLimitedException.Find(ex) is null)
            {
                logger.LogInformation(ex, "Dependency onboarding: the GraphQL collaborator check failed for {Account}; falling back to REST", account.Login);
                graph = null;
            }
            if (graph is null)
            {
                foreach (var repository in chunk)
                    answer[repository.Id!] = await CheckWithRestAsync(token, repository, login, cancellationToken);
                continue;
            }
            foreach (var (id, admin) in graph)
                answer[id] = admin;
        }
        return answer;
    }

    /// <summary>
    /// The aliased query: <c>r0: repository(owner: $o0, name: $n0) { collaborators(login: $login, first: 1) { edges { permission } } }</c>.
    /// Every value is a variable; the aliases are generated (<c>r</c> + index), so nothing a caller supplies is spliced in.
    /// Null when GitHub answers with any error other than a rate limit: the caller falls back to REST.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, bool>?> CheckWithGraphQLAsync(string token, IReadOnlyList<Repository> chunk, string login, CancellationToken cancellationToken)
    {
        var query = new StringBuilder("query($login: String!");
        var variables = new Dictionary<string, object?> { ["login"] = login };
        for (var i = 0; i < chunk.Count; i++)
        {
            query.Append(CultureInfo.InvariantCulture, $", $o{i}: String!, $n{i}: String!");
            variables[$"o{i}"] = chunk[i].OwnerLogin;
            variables[$"n{i}"] = chunk[i].Name;
        }
        query.Append(") {");
        for (var i = 0; i < chunk.Count; i++)
            query.Append(CultureInfo.InvariantCulture, $" r{i}: repository(owner: $o{i}, name: $n{i}) {{ collaborators(login: $login, first: 1) {{ edges {{ permission }} }} }}");
        query.Append(" }");

        var (status, json, headers) = await SendAsync(HttpMethod.Post, "graphql", token, new Dictionary<string, object?> { ["query"] = query.ToString(), ["variables"] = variables }, cancellationToken);
        using (json)
        {
            if (status != HttpStatusCode.OK || json is null)
                return null;
            var root = json.RootElement;
            var errors = Items(root, "errors").Select(e => (Str(e, "type"), Str(e, "message"))).ToList();
            if (GitHubRateLimits.FromGraphQLErrors(errors, headers, DateTimeOffset.UtcNow, "the collaborator check") is { } limited)
                throw new ForgeRequestException("GitHub rate limit reached on the collaborator check.", 429, limited);
            // GraphQL answers per alias: a FORBIDDEN on r1 comes with r0's data (captured live, user token, 2026-10-16:
            // Fixtures/GitHub/Onboarding/graphql-collaborators-partial.json). Only an error that names no alias fails all.
            var failedAliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (var error in Items(root, "errors"))
            {
                var path = Items(error, "path").Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : null).FirstOrDefault();
                if (path is null)
                {
                    logger.LogInformation("Dependency onboarding: GraphQL refused the collaborator check: {Error}", Str(error, "message"));
                    return null;
                }
                failedAliases.Add(path);
            }
            var data = Child(root, "data");
            var answer = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (var i = 0; i < chunk.Count; i++)
            {
                if (failedAliases.Contains($"r{i}"))
                {
                    // This repository's connection was refused (an installation token may not read collaborators): REST.
                    answer[chunk[i].Id!] = await CheckWithRestAsync(token, chunk[i], login, cancellationToken);
                    continue;
                }
                var edges = Items(Child(Child(data, $"r{i}"), "collaborators"), "edges");
                answer[chunk[i].Id!] = edges.Any(e => Str(e, "permission") == "ADMIN");
            }
            return answer;
        }
    }

    internal async Task<bool> CheckWithRestAsync(string token, Repository repository, string login, CancellationToken cancellationToken)
    {
        var (status, json, _) = await SendAsync(HttpMethod.Get,
            $"repos/{Uri.EscapeDataString(repository.OwnerLogin)}/{Uri.EscapeDataString(repository.Name)}/collaborators/{Uri.EscapeDataString(login)}/permission", token, null, cancellationToken);
        using (json)
        {
            if (status == HttpStatusCode.OK && json is not null)
                return Str(json.RootElement, "permission") == "admin";
            // 404: not a collaborator, or a repository the installation cannot see. 403: refused. Either way not an admin.
            if (status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                return false;
            throw new ForgeRequestException($"GitHub answered {(int)status} to the collaborator permission of {repository.FullName}.", (int)status);
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The viewer's token for one listing, refreshed once on a 401 (as <see cref="GitHubAccessService"/> does).</summary>
    private sealed class UserSession(GitHubRepositoryAdministration owner, SparkUser user)
    {
        private string? token;
        private bool refreshed;

        public async Task<(HttpStatusCode Status, JsonDocument? Json)> GetAsync(string path, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (token is null || attempt == 1)
                {
                    var fresh = await owner.tokenService.GetAccessTokenAsync(user, forceRefresh: attempt == 1 && !refreshed, cancellationToken);
                    refreshed |= attempt == 1;
                    if (fresh.State != GitHubTokenState.Ok || fresh.AccessToken is null)
                        return (fresh.State == GitHubTokenState.ReauthRequired ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable, null);
                    token = fresh.AccessToken;
                }
                var (status, json, _) = await owner.SendAsync(HttpMethod.Get, path, token, null, cancellationToken);
                if (status != HttpStatusCode.Unauthorized)
                    return (status, json);
                json?.Dispose();
            }
            return (HttpStatusCode.Unauthorized, null);
        }
    }

    private async Task<(HttpStatusCode Status, JsonDocument? Json, Func<string, string?> Header)> SendAsync(HttpMethod method, string path, string token, object? body,
        CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(nameof(GitHubRepositoryAdministration));
        using var request = new HttpRequestMessage(method, new Uri(ApiBase, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodeCoverage", "1.0"));
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        JsonDocument? json = null;
        if (bytes.Length > 0)
        {
            try { json = JsonDocument.Parse(bytes); }
            catch (JsonException) { /* not JSON: the status decides */ }
        }
        var headers = response.Headers.ToDictionary(h => h.Key, h => h.Value.FirstOrDefault(), StringComparer.OrdinalIgnoreCase);
        string? Header(string name) => headers.TryGetValue(name, out var value) ? value : null;
        var message = json?.RootElement is { ValueKind: JsonValueKind.Object } root ? Str(root, "message") : null;
        if (GitHubRateLimits.Classify(response.StatusCode, Header, message, DateTimeOffset.UtcNow, $"{method} {path.Split('?')[0]}") is { } limited)
        {
            json?.Dispose();
            throw new ForgeRequestException($"GitHub rate limit reached on {method} {path.Split('?')[0]}.", 429, limited);
        }
        return (response.StatusCode, json, Header);
    }

    private static EForgeCredentialState State(GitHubTokenState state) => state switch
    {
        GitHubTokenState.ReauthRequired => EForgeCredentialState.ReauthRequired,
        GitHubTokenState.Unavailable => EForgeCredentialState.Unavailable,
        _ => EForgeCredentialState.Ok,
    };

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Num(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number : null;

    /// <summary>True for a JSON <c>true</c>, and for a present non-null value (<c>suspended_at</c>).</summary>
    private static bool Flag(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
           && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String);

    private static JsonElement Child(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? value : default;

    private static IEnumerable<JsonElement> Items(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    /// <summary>A GitHub login (bot logins included): letters, digits, hyphens, at most 39, plus <c>[bot]</c>.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})(?:\[bot\])?$")]
    private static partial System.Text.RegularExpressions.Regex GitHubLogin();
}

/// <summary>GitHub's <see cref="IForgeSetupRedirect"/>: the App's Setup URL gets <c>installation_id</c> and, undocumented,
/// <c>setup_action</c> (R36).</summary>
[Register(typeof(IForgeSetupRedirect), ServiceLifetime.Singleton)]
public sealed partial class GitHubSetupRedirect : IForgeSetupRedirect
{
    public EForgeProvider Provider => EForgeProvider.GitHub;

    /// <summary>
    /// Any <c>installation_id</c> narrows (D-O13): a usable one to that installation, and a malformed one (empty, negative,
    /// out of range, duplicated, which arrives comma-joined) to an installation that does not exist, so it lists nothing
    /// instead of silently widening to every installation. Only the viewer's own listing ever resolves the key.
    /// </summary>
    public ForgeSetupRedirect? Read(IReadOnlyDictionary<string, string?> query)
    {
        if (!query.TryGetValue("installation_id", out var raw))
            return null;
        var key = long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture)
            : UnknownInstallation;
        var action = query.TryGetValue("setup_action", out var hint) ? hint switch
        {
            "install" => EForgeSetupAction.Install,
            "update" => EForgeSetupAction.Update,
            "request" => EForgeSetupAction.Request,
            _ => EForgeSetupAction.Unknown,
        } : EForgeSetupAction.Unknown;
        return new ForgeSetupRedirect(EForgeProvider.GitHub, key, action);
    }

    /// <summary>The key of a malformed <c>installation_id</c>: no GitHub installation (ids are positive), so it lists nothing.</summary>
    public const string UnknownInstallation = "0";
}
