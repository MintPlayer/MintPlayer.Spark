using CodeCoverage.Forge;
using Microsoft.AspNetCore.Identity;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents;
using System.Linq.Expressions;
using CodeCoverage.Entities;
using CodeCoverage.Indexes;
using CodeCoverage.LookupReferences;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
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
public partial class ApiTokenActions : DefaultPersistentObjectActions<ApiToken>, ISparkOwnsRowSecurity, IBeforeSave<ApiToken>, IAfterSave<ApiToken>
{
    public override async Task OnNewAsync(SparkNewArgs<ApiToken> args)
    {
        await base.OnNewAsync(args);
    }

    public override async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
    {
        var po = await base.OnLoadAsync(id, parent);
        return po;
    }

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
    // Stamps CreatedByUserId. Same pair DeleteDataAction uses to answer "who is asking".
    [Inject] private readonly UserManager<MintPlayer.Spark.Authorization.Identity.SparkUser> userManager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

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
    /// <para>
    /// ⚠️ <c>In()</c> rather than <c>owners.Contains(...)</c> is load-bearing, not style — the same
    /// rule <see cref="RepositoryVisibility"/> and <see cref="GitHubProjectVisibility"/> state. A
    /// <c>string[]</c> receiver binds to the untranslatable <c>MemoryExtensions.Contains</c>, and
    /// RavenDB's LINQ provider throws <c>NotSupportedException: Expression type not supported:
    /// TypedParameterExpression</c> when it translates the query.
    /// </para>
    /// <para>
    /// That is not a theoretical risk: this filter is <b>pushed into the database query</b>
    /// (<c>RowSecurity.ComposeRowFilterAsync</c> does so whenever the element type equals the entity
    /// type, which holds here), so it must be translatable. Written with <c>Contains</c> it took
    /// down both ApiToken query surfaces in production with a bare 500 — while every test stayed
    /// green, because the save path <em>compiles</em> this same expression and runs it in memory,
    /// where <c>MemoryExtensions.Contains</c> is perfectly valid.
    /// </para>
    /// </remarks>
    public override async Task<Expression<Func<ApiToken, bool>>?> GetRowFilterAsync(string action)
    {
        var accountIds = await ManagedAccountIdsAsync();
        // No null guard: In() simply does not match a null field, and adding one back would
        // reintroduce the OrElse/AndAlso shape the provider chokes on.
        return token => token.Account.In(accountIds);
    }

    /// <summary>
    /// The document ids of the accounts the caller manages: the visibility service answers in owner
    /// keys (<c>github:acme</c>), and a token names its account by document id.
    /// </summary>
    /// <remarks>
    /// An owner the caller manages but which has no Account document contributes nothing — it cannot
    /// own a token either, since a token is only created from an account's page.
    /// </remarks>
    private async Task<List<string>> ManagedAccountIdsAsync()
    {
        var owners = await visibility.GetAllowedOwnersAsync();
        if (owners.Length == 0)
            return [];

        var ids = await session.Query<Account, Indexes.Accounts_Overview>()
            .Where(a => a.OwnerKey.In(owners))
            .Select(a => a.Id)
            .ToListAsync();
        return [.. ids.OfType<string>()];
    }

    /// <summary>
    /// Mints the credential on create, and stamps everything the caller must not choose.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The plaintext exists only in this request.</b> Only its SHA-256 is stored, so a
    /// database dump is not a set of working upload credentials — which is why creation cannot be a
    /// plain save of client-supplied fields.
    /// <para>
    /// Ownership is stamped here rather than trusted from the payload. WITH CHECK
    /// runs immediately after this hook and re-applies the row filter to the result, so a create
    /// must produce a row its own caller could see — stamping a login the caller does not manage is
    /// refused rather than saved.
    /// </para>
    /// </remarks>
    public async ValueTask OnBeforeSaveAsync(ApiToken entity, SaveContext context)
    {
        // ⚠️ Runs on EVERY save, create and edit alike — as do the owner authorization and the
        // identity derivation below it. The early return further down is ONLY for the credential,
        // which cannot be re-derived. Everything a later request authorizes on has to be re-derived
        // here, because an edit can change which repositories a token covers AND which account it
        // claims to be.
        // ⚠️ The one field that decides the owner is `Account`, and it is never a posted value. On
        // create it is the account the New was started from: the object's Parent, which Spark resolved from
        // the sub-query and authorized itself. The attribute is read-only, so nothing posted is
        // written to it, and an edit keeps the stored account.
        if (string.IsNullOrEmpty(entity.Hash))
            entity.Account = context.PersistentObject.Parent is { Name: nameof(Account), Id: { Length: > 0 } parentId } ? parentId : null;

        // Re-authorized on every save, edit included: the caller's membership of the account can
        // have been revoked since the token was minted.
        var account = entity.Account is { Length: > 0 } accountId ? await session.LoadAsync<Account>(accountId) : null;
        if (account is null || !await visibility.CanManageOwnerAsync(account.OwnerKey))
            throw new SparkValidationException(nameof(ApiToken.Account), "You do not manage that account.");

        await ValidateRepositoryScopeAsync(entity, account);
        entity.Scope = entity.RepositoryIds.Count > 0 ? ApiTokenScope.Repository : ApiTokenScope.Account;

        if (!string.IsNullOrEmpty(entity.Hash))
            return; // An edit; the credential is already minted and cannot be re-derived.

        plaintext = ApiTokenService.GenerateTokenValue();
        entity.Hash = ApiTokenService.Hash(plaintext);

        // Stamped, never trusted from the payload: the attribute is read-only in the model, so a
        // posted value is refused by IsWritableBySchema anyway, but the field was previously
        // writable AND stamped by nothing — every token made through the UI carried an empty string.
        var principal = httpContextAccessor.HttpContext?.User;
        var creator = principal is null ? null : await userManager.GetUserAsync(principal);
        entity.CreatedByUserId = creator?.Id ?? string.Empty;
        entity.CreatedAtUtc = DateTime.UtcNow;
        entity.RevokedAtUtc = null;
    }

    /// <summary>
    /// Refuses a token scoped to a repository that is not the token's own account's.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Nothing else checks this.</b> A reference ARRAY is written straight through
    /// (<c>EntityMapper</c> hands the posted value to the property and returns), bypassing the
    /// collection guard that binds a scalar reference's id to its type — so whatever ids the client
    /// posts are what get stored. And WITH CHECK re-applies only the
    /// <c>Account</c> row filter, which says nothing about repository ownership.
    /// <para>
    /// Without this, any signed-in user could mint a token for any repository by posting its
    /// document id. Each repository must belong to <paramref name="account"/> — the account the caller
    /// was just authorized for — which is narrower than "some account the caller manages": a token
    /// for one account cannot reach into another, even one the same caller also manages.
    /// </para>
    /// </remarks>
    private async Task ValidateRepositoryScopeAsync(ApiToken entity, Account account)
    {
        if (entity.RepositoryIds.Count == 0)
            return;

        // Distinct, because a duplicated id would otherwise mean a repeated claim on the wire.
        entity.RepositoryIds = [.. entity.RepositoryIds.Distinct(StringComparer.Ordinal)];

        var repositories = await session.LoadAsync<Repository>(entity.RepositoryIds);

        foreach (var id in entity.RepositoryIds)
        {
            repositories.TryGetValue(id, out var repository);

            // Unknown and foreign are refused identically — a caller must not be able to discover
            // which repository ids exist by comparing error messages.
            if (repository is null || !string.Equals(repository.Account, account.Id, StringComparison.Ordinal))
            {
                throw new SparkValidationException(
                    nameof(ApiToken.RepositoryIds),
                    "One of the selected repositories is not one of this account's.");
            }
        }
    }

    /// <summary>
    /// Hands the caller the one and only copy of the plaintext.
    /// </summary>
    /// <remarks>
    /// ⚠️ After the save, not before: a token the user has written down but which failed to store
    /// is worse than no token. It goes out as a notification because it is deliberately not a field
    /// — <c>ApiToken</c> has nowhere to put it, and adding one would store it.
    /// </remarks>
    public ValueTask OnAfterSaveAsync(ApiToken entity, SaveContext context)
    {
        if (plaintext is not null)
        {
            manager.Client.Notify(
                $"Copy this now, it is shown once: {plaintext}",
                NotificationKind.Success,
                TimeSpan.FromMinutes(10));
            plaintext = null;
        }

        return ValueTask.CompletedTask;
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
    [NoInterfaceMember]
    public IQueryable<ApiToken> Account_UploadTokens(CustomQueryArgs args)
    {
        args.EnsureParent("Account");

        // By document id, the field the token stores: through the index, so each row also carries
        // the account's current login and key (VApiToken).
        return session.Query<VApiToken, ApiTokens_Overview>()
            .Where(t => t.Account == args.Parent!.Id)
            .OfType<ApiToken>();
    }
}
