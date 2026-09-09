using Raven.Client.Documents.Session;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents;
using System.Linq.Expressions;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using CodeCoverage.ApiTokens;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;

namespace CodeCoverage.Actions;

/// <summary>
/// Upload tokens are visible only to the people who manage the account they upload for.
/// </summary>
/// <remarks>
/// ⚠️ <b>This row filter is not a refinement, it is the access control.</b> Registering
/// <c>ApiToken</c> in the context created a query endpoint and a per-object endpoint over a
/// collection of credentials; the type-level grant in <c>security.json</c> is
/// <c>Authenticated</c>, because every signed-in user manages <em>some</em> account. Without a row
/// filter that means every signed-in user can list everyone else's tokens.
/// <para>
/// The rule is the one <c>TokensController</c> already enforced per call — "you manage this
/// account" — moved to where the framework applies it to every read path at once, rather than
/// re-implemented per endpoint.
/// </para>
/// <para>
/// What it does <b>not</b> protect is the token hash: that is kept off the wire by
/// <c>[IgnoreProperty]</c> on <c>ApiToken.Hash</c>, so it is never projected regardless of who can
/// see the row. Two independent things, deliberately — a filter can be got wrong, and the hash
/// should not depend on it.
/// </para>
/// </remarks>
public partial class ApiTokenActions : DefaultPersistentObjectActions<ApiToken>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "An upload token is a credential for one account, so only that account's managers may see " +
        "that it exists, what it is called, and when it was created or revoked. The grant in " +
        "security.json is Authenticated — every signed-in user manages something — so the row " +
        "filter, not the grant, is what separates one user's tokens from another's. The token hash " +
        "itself is never projected at all: ApiToken.Hash carries [IgnoreProperty], which keeps it " +
        "out of the model rather than relying on this filter.";

    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IManager manager;

    /// <summary>
    /// Restricted to the accounts the caller manages, for every action.
    /// </summary>
    /// <remarks>
    /// No per-action divergence, unlike <c>Repository</c>: there is no shareable-link case here, so
    /// a detail page has no reason to resolve for a token the caller may not list.
    /// <para>
    /// ⚠️ An empty owner set produces a filter that matches nothing, which is the correct reading
    /// of "signed in, manages nothing" — and is why this must return a filter rather than
    /// <see langword="null"/> for that case.
    /// </para>
    /// </remarks>
    public override async Task<Expression<Func<ApiToken, bool>>?> GetRowFilterAsync(string action)
    {
        var owners = await visibility.GetAllowedOwnersAsync();
        return token => token.AccountLogin != null && owners.Contains(token.AccountLogin);
    }

    /// <summary>
    /// Mints the credential on create, and stamps everything the caller must not choose.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The plaintext exists only in this request.</b> Only its SHA-256 is stored, so a
    /// database dump is not a set of working upload credentials — which is why creation cannot be a
    /// plain save of client-supplied fields.
    /// <para>
    /// Ownership is stamped here rather than trusted from the payload. <c>EnsureRowSaveAllowedAsync</c>
    /// runs immediately after this hook and re-applies the row filter to the result, so a create
    /// must produce a row its own caller could see — stamping a login the caller does not manage is
    /// refused rather than saved.
    /// </para>
    /// </remarks>
    public override async Task OnBeforeSaveAsync(PersistentObject obj, ApiToken entity)
    {
        if (!string.IsNullOrEmpty(entity.Hash))
            return; // An edit; the credential is already minted and cannot be re-derived.

        var login = entity.AccountLogin;
        if (string.IsNullOrWhiteSpace(login) || !await visibility.CanManageOwnerAsync(login))
            throw new SparkValidationException(nameof(ApiToken.AccountLogin), "You do not manage that account.");

        var account = await session.Query<Account>().FirstOrDefaultAsync(a => a.Login == login);

        plaintext = ApiTokenService.GenerateTokenValue();
        entity.Hash = ApiTokenService.Hash(plaintext);
        entity.Scope = entity.RepositoryGitHubId is null ? "Account" : "Repository";
        entity.AccountGitHubId = account?.GitHubId;
        entity.CreatedAtUtc = DateTime.UtcNow;
        entity.RevokedAtUtc = null;
    }

    /// <summary>
    /// Hands the caller the one and only copy of the plaintext.
    /// </summary>
    /// <remarks>
    /// ⚠️ After the save, not before: a token the user has written down but which failed to store
    /// is worse than no token. It goes out as a notification because it is deliberately not a field
    /// — <c>ApiToken</c> has nowhere to put it, and adding one would store it.
    /// </remarks>
    public override Task OnAfterSaveAsync(PersistentObject obj, ApiToken entity)
    {
        if (plaintext is not null)
        {
            manager.Client.Notify(
                $"Copy this now, it is shown once: {plaintext}",
                NotificationKind.Success,
                TimeSpan.FromMinutes(10));
            plaintext = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>Set by the create path, consumed once by <see cref="OnAfterSaveAsync"/>.</summary>
    private string? plaintext;

    /// <summary>
    /// Custom query: the upload tokens of one account, parent-scoped. Source
    /// <c>Custom.Account_UploadTokens</c>, rendered as a tab on the account page.
    /// </summary>
    /// <remarks>
    /// A <c>Custom.*</c> source rather than <c>Database.*</c> because a Database query drops
    /// parentId, so it would list every token instead of the account's. The framework still applies
    /// <see cref="GetRowFilterAsync"/> and the sort on top of what this returns — the parent scope
    /// here is about <em>which</em> account is being shown, not about who may see it.
    /// </remarks>
    public IRavenQueryable<ApiToken> Account_UploadTokens(CustomQueryArgs args)
    {
        args.EnsureParent("Account");

        // Matched on login rather than the numeric id: AccountGitHubId is null on tokens issued
        // before that field existed, and the same fallback is what ApiTokenAuthenticationHandler
        // relies on so a deploy never invalidates a working token.
        var login = args.Parent!.Attributes
            .FirstOrDefault(a => a.Name == nameof(Account.Login))?.Value?.ToString();

        return session.Query<ApiToken>().Where(t => t.AccountLogin == login);
    }
}
