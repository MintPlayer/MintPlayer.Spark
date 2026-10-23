using System.Linq.Expressions;
using Raven.Client.Documents.Linq;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;

namespace CodeCoverage.Actions;

/// <summary>
/// An account's public face (login, avatar, connection) is public to READ; its settings are its managers' and the
/// Administrators' alone; WRITES are its managers' alone. The installation id never leaves the server
/// (<c>[IgnoreProperty]</c>, #264).
/// </summary>
/// <remarks>
/// The layering (dependency-updates PRD §13.12, D-A1–D-A8): <c>security.json</c> grants <c>QueryRead/Account</c> to
/// everyone, <c>Edit/Account</c> to signed-in users and <c>ReadSettings/Account</c> to the Administrators, and this class
/// narrows per request — the settings are protected attributes for anyone who neither manages the account nor holds
/// ReadSettings, Edit is withheld (and filtered) on an account the caller does not manage, and Query (the account grid)
/// is withheld from anyone without ReadSettings. Narrowing never widens: nothing here grants what <c>security.json</c>
/// does not.
/// </remarks>
public partial class AccountActions : DefaultPersistentObjectActions<Account>, ISparkOwnsRowSecurity, MintPlayer.Spark.Abstractions.Interceptors.IBeforeSave<Account>, MintPlayer.Spark.Abstractions.Interceptors.IAfterSaveCommitted<Account>
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Unfiltered for READS, filtered for WRITES, redacted per account. An account's login, avatar and connection are " +
        "data the forge already publishes, and coverage.mintplayer.com is a public dashboard, so security.json grants " +
        "QueryRead/Account to the anonymous role on purpose (the account page is public; the grid is withheld from non-administrators by OnDisableActionsAsync). Everything else on an account is " +
        "its owner's configuration, and GetProtectedAttributesAsync blanks it for anyone who neither manages the account " +
        "nor is an administrator (D-A3). Edit/Account is granted to signed-in users so an owner can change those " +
        "settings, which makes the write path reachable: without a filter, RowSecurity treats a missing rule as ALLOWED, " +
        "so writes are narrowed to accounts the caller manages, and Edit is withheld on the rest. The installation id is " +
        "[IgnoreProperty] on the entity and so never reaches the wire at all.";

    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly MintPlayer.Spark.Messaging.Abstractions.IMessageBus messageBus;
    [Inject] private readonly Raven.Client.Documents.IDocumentStore documentStore;
    [Inject] private readonly ILogger<AccountActions> logger;
    [Inject] private readonly IManager manager;

    /// <summary>
    /// R23: the account default's transition, for the after-commit hook (which gets facts, never the entity). The PR size
    /// labeller's decision rides along the same way (#527 D4.3); switching it off may ask first, so a retry reruns this
    /// from the top, and nothing before the question has a side effect.
    /// </summary>
    public async ValueTask OnBeforeSaveAsync(Account entity, MintPlayer.Spark.Abstractions.Interceptors.SaveContext context)
    {
        PrSize.PrSizeValidation.ValidateAccount(entity);

        if (DependencyUpdateSwitch.Transition(context.Before as Account, entity) is { } transition)
            context.Facts[DependencyUpdateSwitch.Fact] = transition;

        using var session = documentStore.OpenAsyncSession();
        if (await PrSize.PrSizeLabelSwitch.DecideAsync(manager, session, context.Before as Account, entity) is { } decision)
            context.Facts[PrSize.PrSizeLabelSwitch.Fact] = decision;
    }

    /// <summary>
    /// R23/R24: the account's default switched, committed: every connected repository that inherits it is onboarded
    /// (off → on) or offboarded (on → off), not expedited. Progress shows on the Dependency updates page.
    /// </summary>
    public async Task OnAfterSaveCommittedAsync(MintPlayer.Spark.Abstractions.Interceptors.SparkCommittedChange change, CancellationToken cancellationToken)
    {
        using var session = documentStore.OpenAsyncSession();
        if (change.Facts.TryGetValue(DependencyUpdateSwitch.Fact, out var transition))
        {
            var count = await DependencyUpdateSwitch.FanOutAsync(session, messageBus, change.Id, transition, change.OccurredAt, cancellationToken);
            logger.LogInformation("Dependency updates: {Transition} for account {Account}, fanned out over {Count} repositories", transition, change.Id, count);
        }
        // #527 D4.2/D4.3: a changed scale reconciles, a disable with "Remove labels" removes; both on the labeller's lane.
        if (change.Facts.TryGetValue(PrSize.PrSizeLabelSwitch.Fact, out var decision))
        {
            var count = await PrSize.PrSizeLabelSwitch.FanOutAsync(session, messageBus, change.Id, decision, cancellationToken);
            logger.LogInformation("PR size labels: {Decision} for account {Account}, fanned out over {Count} repositories", decision, change.Id, count);
        }
    }

    /// <summary>
    /// No filter for reads; managers only for writes.
    /// </summary>
    /// <remarks>
    /// ⚠️ Returning <see langword="null"/> means UNRESTRICTED, not "denied" — <c>RowSecurity</c>
    /// evaluates a missing rule as allowed. That is correct for the public read surface and would be
    /// a hole on the write one, which is why the two are answered separately here rather than by one
    /// expression.
    /// </remarks>
    public override async Task<Expression<Func<Account, bool>>?> GetRowFilterAsync(string action)
    {
        if (action is "Query" or "Read")
            return null;

        // `.In()` rather than Contains: this predicate is pushed into RQL, where Contains is
        // untranslatable — see ApiTokenActions for the production outage that taught us.
        // The MANAGED owners (R39, D-O12), not the owner set: a read-only member of an organization reaches its installation
        // but does not manage the account.
        var owners = await visibility.GetManagedOwnersAsync();
        return account => account.OwnerKey.In(owners);
    }

    /// <summary>
    /// Everything but the public face, for anyone who may not read this account's settings (D-A3).
    /// </summary>
    /// <remarks>
    /// Deny by default: <see cref="OwnerSettings.ProtectedAccountAttributes"/> is every property NOT on the public list, so
    /// a setting added later is protected without anybody remembering to say so. Spark blanks these on every read path —
    /// the detail load, query rows, breadcrumbs, refresh — and shields them from write-back.
    /// </remarks>
    public override async Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Account entity)
        => await OwnerSettings.CanReadAsync(visibility, permissions, entity.OwnerKey, nameof(Account))
            ? null
            : OwnerSettings.ProtectedAccountAttributes;

    /// <summary>
    /// Withholds Edit (and Save) on an account the caller does not manage (D-A4): the per-request narrowing of the
    /// <c>Edit/Account</c> grant. An administrator reads every account's settings but manages none, so is withheld it too.
    /// </summary>
    /// <remarks>
    /// The write row filter above stays: it is what turns an edit of somebody else's account into the same 404 as a
    /// missing one, and this hook is asked only after that gate. What this adds is the page's answer — the Edit button is
    /// not offered — from the same rule, so the page and the submit cannot disagree.
    /// </remarks>
    public override async Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context is { TargetKind: DisableActionsTargetKind.PersistentObject, Entity: Account account }
            && !await visibility.CanManageOwnerAsync(account.OwnerKey))
        {
            target.DisableActions(SparkCoreActions.Edit, "Save");
        }

        // The account grid is the Administrators' alone (D-A2). security.json cannot say so: Read implies Query in Spark
        // (#324, SparkRightImplications), and the account page — public — needs Read. So the grant stays QueryRead and
        // the listing is narrowed here. It is a real narrowing, not cosmetics: a grid sorts and filters on stored values,
        // which per-row redaction cannot hide (Spark decides ordering and filtering per query, before rows exist), so a
        // listing would be an oracle for every setting it can sort or filter by. No page needs it: the account page is a
        // Read, and every reference to an account is a breadcrumb (Read) on a read-only attribute.
        if (context.TargetKind == DisableActionsTargetKind.Query
            && !await permissions.IsAllowedAsync(OwnerSettingsRights.ReadSettings, nameof(Account)))
        {
            target.DisableActions(SparkCoreActions.Query);
        }
    }
}
