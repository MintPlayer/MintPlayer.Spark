namespace CodeCoverage.Services;

/// <summary>
/// The signed-in user's accounts, joined with what this app knows about them.
/// <para>
/// One implementation behind two callers that must not drift: <c>/api/me/accounts</c>
/// (the JSON the SPA's own pages use) and the <c>Custom.MyAccounts</c> Spark query
/// backing the composed Home page. Before this existed the aggregation lived in
/// <c>MeController</c> alone, and a second copy in a query action would have been
/// two ways to compute "how covered is this account" — divergent the first time
/// either changed.
/// </para>
/// </summary>
public interface IMyAccountsService
{
    /// <summary>
    /// Every account the caller may administer, ordered by login. Empty when the
    /// caller is anonymous or GitHub reports no owners.
    /// </summary>
    /// <param name="waitForNonStaleResults">
    /// Wait for the indexes this reads to catch up before querying them. Off by default, because
    /// every ordinary read of this page would pay for it and none of them needs it.
    /// <para>
    /// Set it when calling immediately after a write whose effect must be visible in the result —
    /// the resync button is the case that matters. RavenDB indexes are eventually consistent, so
    /// reading straight back after <c>SaveChangesAsync</c> returns the pre-write state, and the
    /// button then reports numbers that contradict the work it just did. That reads as the button
    /// having done nothing at all.
    /// </para>
    /// </param>
    Task<MyAccountsResult> GetAsync(CancellationToken cancellationToken, bool waitForNonStaleResults = false);
}

/// <param name="GitHubAppUrl">The environment's GitHub App public page, so an "install the App"
/// link points at the right App rather than at a hardcoded slug.</param>
/// <param name="ReauthRequired">The stored GitHub token is dead and silent refresh failed — only
/// a browser round-trip can fix it. While set, <paramref name="Accounts"/> is degraded to the
/// user's own account.</param>
public sealed record MyAccountsResult(string GitHubAppUrl, MyAccountRow[] Accounts, bool ReauthRequired);

/// <summary>
/// One row of the accounts list.
/// <para>
/// Deliberately not an entity: it is an aggregate over Account and Repository documents that
/// exists only for the duration of a request. <c>Id</c> is <c>Login</c> — a query result row must
/// carry a readable, unique id or the grid collapses, and login is already unique per account.
/// </para>
/// </summary>
public sealed record MyAccountRow(
    string Id,
    string Login,
    string Type,
    string? AvatarUrl,
    int RepoCount,
    double? AggregateCoverage,
    bool IsAppInstalled);
