using System.Linq.Expressions;
using CodeCoverage.Entities;
using CodeCoverage.LookupReferences;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Actions;

/// <summary>
/// Row security for the generic /spark surface — same read semantics as
/// BrowseController.ResolveVisibleRepository: anonymous viewers see public
/// repositories; authenticated viewers additionally the repos of owners GitHub
/// grants them.
/// <para>
/// ⚠️ The write path is now reachable. <c>Edit/Repository</c> is granted so an owner can set
/// <c>DeleteBranchOnPrClose</c>, which means <c>GetRowFilterAsync</c> is what stands between a
/// signed-in user and somebody else's repository settings — it is not a refinement of a read rule,
/// it is the access control. Only <c>DeleteBranchOnPrClose</c> is writable at all: every other
/// attribute is <c>isReadOnly</c> in the model, which <c>EntityMapper.IsWritableBySchema</c>
/// enforces server-side regardless of what a client posts.
/// </para>
/// </summary>
public partial class RepositoryActions : DefaultPersistentObjectActions<Repository>
{
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>
    /// Withholds <c>DeleteData</c> on a repository that is still connected.
    /// <para>
    /// The right is granted to every signed-in user in security.json, and it has to be: rights
    /// here are group-level and there is no group per GitHub owner, so the right can express
    /// "may delete coverage data at all" and nothing narrower. Whether THIS repository may be
    /// deleted is a property of the row — it must be disconnected — and the actions catalogue at
    /// <c>POST /spark/actions/list</c> is per type, so it cannot answer that.
    /// </para>
    /// <para>
    /// This is the place that can: the entity is in hand, so the answer travels back on the object
    /// itself and the browser simply never renders the button. <c>DeleteDataAction</c> still
    /// refuses independently — withholding an affordance is not a permission check, and the
    /// endpoint stays reachable — but a user is no longer offered an irreversible red button on a
    /// healthy repository that only admits it will refuse after the confirmation prompt.
    /// </para>
    /// </summary>
    public override async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
    {
        var obj = await base.OnLoadAsync(id, parent);
        if (obj is null)
            return obj;

        var repository = await session.LoadAsync<Repository>(id);
        if (repository is null || repository.Connection != RepositoryConnection.Disconnected)
            obj.DisableActions("DeleteData");

        return obj;
    }

    /// <summary>
    /// The gate's business rules, enforced on the object that actually saves.
    /// <para>
    /// These moved here verbatim from <c>RepoSettingsController.PutGate</c>, which this work
    /// deleted along with the hand-written panel that called it. They must live on Repository and
    /// not on <c>GateSettingsActions</c>: a gate is embedded in its Repository's document, so it has
    /// no save of its own and a hook there would never run.
    /// </para>
    /// <para>
    /// ⚠️ <c>GateSettingsActions.OnRefreshAsync</c> makes <c>ProjectTarget</c> required in fixed
    /// mode, and that is a <b>presentation</b> rule only — Spark re-derives refresh rules on save for
    /// a root type, never for an AsDetail row. This method is what actually holds a client to it, so
    /// the two must be changed together.
    /// </para>
    /// </summary>
    public override Task OnBeforeSaveAsync(PersistentObject obj, Repository entity)
    {
        var gate = entity.Gate;
        if (gate is null)
            return base.OnBeforeSaveAsync(obj, entity);

        // A form posts every attribute, including the ones nobody touched, so an unset dropdown
        // arrives as null rather than as the property initializer's value — that initializer only
        // ever runs for `new GateSettings()`. The deleted REST endpoint never met this because its
        // GET handed the panel a fully populated body to send back.
        //
        // "Empty means every default" is the documented meaning of an unset gate, so fill the
        // blanks rather than refuse them. Writing them explicitly is deliberate: the stored document
        // then says what it does, and a later default change cannot silently re-judge old builds.
        if (string.IsNullOrEmpty(gate.ProjectMode))
            gate.ProjectMode = ProjectComparison.Auto;
        if (string.IsNullOrEmpty(gate.ProjectBasis))
            gate.ProjectBasis = LookupReferences.ProjectBasis.Scoped;

        if (gate.ProjectMode is not (ProjectComparison.Auto or ProjectComparison.Fixed))
            throw new SparkValidationException(
                "Project comparison must be auto or fixed.", nameof(GateSettings.ProjectMode));

        if (gate.ProjectBasis is not (LookupReferences.ProjectBasis.Scoped or LookupReferences.ProjectBasis.Projection))
            throw new SparkValidationException(
                "Partial builds judge must be scoped or projection.", nameof(GateSettings.ProjectBasis));

        if (gate.ProjectTarget is < 0 or > 100)
            throw new SparkValidationException(
                "Project target is a percentage (0-100).", nameof(GateSettings.ProjectTarget));

        if (gate.PatchTarget is < 0 or > 100)
            throw new SparkValidationException(
                "Patch target is a percentage (0-100).", nameof(GateSettings.PatchTarget));

        if (gate.ProjectThreshold is < 0 or > 100)
            throw new SparkValidationException(
                "Allowed drop is in percentage points (0-100).", nameof(GateSettings.ProjectThreshold));

        if (gate.PatchThreshold is < 0 or > 100)
            throw new SparkValidationException(
                "Patch tolerance is in percentage points (0-100).", nameof(GateSettings.PatchThreshold));

        // The one genuinely conditional rule, and the reason a declarative model rule could not
        // replace this method: it is a statement about two attributes at once.
        if (gate.ProjectMode == ProjectComparison.Fixed && gate.ProjectTarget is null)
            throw new SparkValidationException(
                "A fixed comparison needs a project target.", nameof(GateSettings.ProjectTarget));

        return base.OnBeforeSaveAsync(obj, entity);
    }

    public override async Task<Expression<Func<Repository, bool>>?> GetRowFilterAsync(string action)
    {
        // Empty for anonymous viewers → the read filters reduce to "public only".
        var owners = await visibility.GetAllowedOwnersAsync();

        // ⚠️ Writes get a different filter, and the difference IS the access control.
        //
        // This method used to answer every non-Query action with Filter(owners), on the stated
        // grounds that "writes are denied at the type level (no Edit/New/Delete right in
        // security.json)". Granting Edit/Repository — so an owner can set DeleteBranchOnPrClose —
        // made that false, and the WITH CHECK path (DatabaseAccess + EnsureRowSaveAllowedAsync)
        // compiles this same predicate: the read filter admits any PUBLIC repository, so leaving it
        // in place would have let any signed-in user edit any public repository's settings.
        //
        // Owners only, no public tier. An empty owner set matches nothing, which is the correct
        // reading of "signed in, manages nothing". `.In()` rather than Contains -- see
        // ApiTokenActions for what a non-translatable predicate costs.
        if (action is not ("Query" or "Read"))
            return repository => repository.OwnerKey.In(owners);

        // The one place the two READ rules diverge. "Query" is the grid — a listing, which must stop
        // advertising a repository we have lost access to. "Read" is the detail page, which is
        // where /{provider}/r/{owner}/{name} lands, so it has to keep resolving for a disconnected repository
        // or every shared report link and every README badge dies with the transfer.
        return action == "Query"
            ? RepositoryVisibility.ListingFilter(owners)
            : RepositoryVisibility.Filter(owners);
    }

    /// <summary>
    /// The repositories a caller may scope an upload token to — the option list behind
    /// <c>ApiToken.RepositoryIds</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Parent-free, deliberately.</b> A reference picker sends the FORM's own parent, so an
    /// ApiToken form sends <c>parentType=ApiToken</c> — or nothing at all on the New form.
    /// <c>Account_Repositories</c> calls <c>EnsureParent("Account")</c>, which would throw and
    /// surface as a 500 from the picker rather than an empty dropdown. <c>ProjectColumnActions</c>
    /// records being bitten by exactly this.
    /// <para>
    /// Scoped to owners the caller manages rather than relying on the row filter: the filter's
    /// <c>Query</c> arm is the LISTING rule, which admits public repositories, and an option list
    /// offering repositories the caller cannot actually scope a token to would only produce a
    /// validation error after they picked one.
    /// </para>
    /// </remarks>
    public async Task<IRavenQueryable<Repository>> ApiToken_SelectableRepositories(CustomQueryArgs args)
    {
        var owners = await visibility.GetAllowedOwnersAsync();
        return session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.OwnerKey.In(owners));
    }

    /// <summary>BadgeToken grants badge access on private repos — managers only.</summary>
    public override async Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Repository entity)
        => await visibility.CanManageOwnerAsync(entity.OwnerKey)
            ? null
            : [nameof(Repository.BadgeToken)];

    public override IReadOnlyCollection<string>? GetDefaultIncludes() => [nameof(Repository.Account)];

    /// <summary>
    /// Custom query: repositories of an account, parent-scoped. Source:
    /// "Custom.Account_Repositories". A Custom.* source because Database.*
    /// queries drop parentId upstream (Spark#242); the framework still applies
    /// the row filter and sorting on top.
    /// </summary>
    public IRavenQueryable<Repository> Account_Repositories(CustomQueryArgs args)
    {
        args.EnsureParent("Account");
        return session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.Account == args.Parent!.Id);
    }
}
