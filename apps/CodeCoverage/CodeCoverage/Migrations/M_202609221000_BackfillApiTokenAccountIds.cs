using MintPlayer.Spark;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;

namespace CodeCoverage.Migrations;

/// <summary>
/// Stamps <see cref="ApiToken.AccountId"/> on account-scoped tokens that predate the field,
/// so the login-comparison fallback in the upload authorization can eventually be removed — M6g.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the numeric id and not the owner key.</b> <c>AccountOwnerKey</c> is
/// <c>provider:login</c> — login-derived, so it carries exactly the weakness the numeric id exists
/// to avoid. <c>UploadsController</c> compares <c>repository.Account == Accounts/github/{ownerId}</c>
/// precisely because a login comparison is wrong <em>in both directions</em> once a repository is
/// transferred: the old owner's token keeps working for a repository they no longer own, and the
/// new owner's does not work for one they do. Standing authorization on the key instead would be a
/// regression dressed as a simplification.
/// </para>
/// <para>
/// ⚠️ <b>This migration cannot resolve every token, and that is the point of running it.</b> The
/// unresolvable ones are the exact population the fallback protects, so they have to be counted
/// before anyone deletes it. Four causes, none repairable from inside the database:
/// </para>
/// <list type="bullet">
/// <item><description><b>No owner key at all</b> — the #422 pass set it only where
/// <c>AccountLogin</c> was present, so a token with neither got neither.</description></item>
/// <item><description><b>The account was renamed.</b> The stored login no longer names any
/// <c>Account</c>; the numeric id it should map to is the one thing a rename preserves and the one
/// thing we did not record.</description></item>
/// <item><description><b>The account was deleted, or the app uninstalled</b> — no <c>Account</c>
/// document survives to resolve from.</description></item>
/// <item><description><b>The app never had an installation on that owner.</b> <c>Account</c>
/// documents originate from installation webhooks, so an account-scoped token can predate or
/// outlive one.</description></item>
/// </list>
/// <para>
/// It <b>logs the unresolvable count at warning level and names them</b> rather than leaving a
/// silent null. Silence is what blocked M6g in the first place: the field was assumed backfilled,
/// nothing checked, and the milestone sat on a precondition nobody had tested. ⚠️ It does
/// <b>not</b> revoke them — a token that still works is one somebody is using, and breaking an
/// upload pipeline during a migration is not a decision a migration should take on its own.
/// </para>
/// <para>
/// Repository-scoped tokens are skipped: they authorize on the repository claims, never on the
/// account, so the field means nothing for them.
/// </para>
/// </remarks>
public partial class M_202609221000_BackfillApiTokenAccountIds : ISparkMigration
{
    public static long Version => 202609221000;
    public static string? Description => "ApiToken.AccountId, so the login fallback can go";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ILogger<M_202609221000_BackfillApiTokenAccountIds> logger;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var accountsByKey = await LoadAccountIdsByOwnerKeyAsync(cancellationToken);

        using var session = store.OpenAsyncSession();

        // ⚠️ One `LoadAsync` per resolvable token, and a RavenDB session allows 30 requests. Without
        // this the migration THROWS on the 30th token, the version marker is never written, startup
        // aborts — and the next start fails at exactly the same place. A deterministic restart loop
        // in which the app never serves. The repo's own idiom, used in six other places.
        using var requestScope = session.IgnoreMaxRequests(logger: logger);

        var stamped = 0;
        var unresolvable = new List<string>();

        await using var stream = await session.Advanced.StreamAsync<ApiToken>(
            startsWith: "ApiTokens/", token: cancellationToken);

        while (await stream.MoveNextAsync())
        {
            var token = stream.Current.Document;

            // Already stamped, or scoped in a way that never reads the field.
            if (token.AccountId is not null || token.Scope != "Account")
                continue;

            // ⚠️ Resolve through the owner KEY, not the bare login. Two forges can host the same
            // login, and stamping a token with the wrong forge's numeric id would authorize uploads
            // for somebody else's repositories — the precise failure an upload credential must not
            // have. A token with no key is unresolvable rather than resolvable-by-login.
            if (token.AccountOwnerKey is { Length: > 0 } key
                && accountsByKey.TryGetValue(key, out var accountId))
            {
                // Loaded through the session so the change is tracked, not through the stream.
                var tracked = await session.LoadAsync<ApiToken>(token.Id, cancellationToken);
                tracked.AccountId = accountId;
                stamped++;

                // Bounds the tracked-entity set, not the request count — `Clear()` does not reset
                // `NumberOfRequests`, which is why the scope above is what actually keeps this
                // alive.
                if (stamped % 256 == 0)
                {
                    await session.SaveChangesAsync(cancellationToken);
                    session.Advanced.Clear();
                }
            }
            else
            {
                unresolvable.Add($"{token.Id} ({token.Description ?? "no description"}, owner {token.AccountOwnerKey ?? "unknown"})");
            }
        }

        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Backfilled AccountId on {Stamped} account-scoped API tokens.", stamped);

        if (unresolvable.Count > 0)
        {
            // ⚠️ Warning, not information, and the ids are named. These are the tokens that keep the
            // login fallback alive; M6g cannot delete it until this list is empty or each entry has
            // been dealt with deliberately.
            logger.LogWarning(
                "{Count} account-scoped API tokens could not be resolved to an account id and still "
                + "depend on the login fallback: {Tokens}",
                unresolvable.Count, string.Join("; ", unresolvable.Take(50)));
        }
    }

    /// <summary>
    /// Every account's numeric id, keyed by its <c>provider:login</c> owner key.
    /// </summary>
    /// <remarks>
    /// ⚠️ A client-side join, not a patch script, and it has to be: the natural id
    /// <c>Accounts/github/{GitHubId}</c> is keyed by the very value being looked up, so there is no
    /// id to <c>load()</c> from a token. The <c>Accounts</c> collection is small — one document per
    /// installation — so holding it in memory is cheap.
    /// </remarks>
    private async Task<Dictionary<string, long>> LoadAccountIdsByOwnerKeyAsync(CancellationToken cancellationToken)
    {
        var byKey = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        using var session = store.OpenAsyncSession();
        await using var stream = await session.Advanced.StreamAsync<Account>(
            startsWith: "Accounts/", token: cancellationToken);

        while (await stream.MoveNextAsync())
        {
            var account = stream.Current.Document;
            if (account.Login is { Length: > 0 })
                byKey[new ForgeOwner(account.Provider, account.Login).ToString()] = account.GitHubId;
        }

        return byKey;
    }
}
