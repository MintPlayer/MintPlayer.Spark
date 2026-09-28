using System.Linq.Expressions;
using System.Security.Claims;

namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// A cross-cutting row rule that applies to many entity types at once — soft deletion, tenancy, a
/// moderation lock — without every Actions class having to repeat it (#460, item 1).
/// </summary>
/// <remarks>
/// <para>
/// Implement <see cref="IRowFilterPolicy"/> (a predicate the framework pushes into the database) or
/// <see cref="IRowCheckPolicy"/> (a per-row check after materialization), usually through the typed
/// helpers <see cref="RowFilterPolicy{T}"/> / <see cref="RowCheckPolicy{T}"/>, and register with
/// <c>AddSparkRowPolicy&lt;TPolicy&gt;()</c>. Policies are scoped: one instance per request.
/// </para>
/// <para>
/// A policy composes with the type's own rule (<c>GetRowFilterAsync</c> / <c>IsAllowedAsync</c> on
/// its Actions class) by <b>AND</b>, and with every other applicable policy the same way. It can only
/// ever narrow what a caller sees; nothing a policy returns widens it.
/// </para>
/// <para>
/// ⚠️ Every path that asks row security applies policies — list, detail, custom queries, sub-queries,
/// distinct values, streams, breadcrumbs, the edit/delete gates, custom-action selections and the
/// after-save WITH CHECK. The known exception is an Actions class that overrides <c>OnLoadAsync</c> or
/// <c>OnSaveAsync</c> without calling the base: that override takes over the row gate and WITH CHECK
/// for its type, policies included (D1 — documented, not fixed).
/// </para>
/// </remarks>
public interface IRowPolicy
{
    /// <summary>
    /// Whether this policy governs <paramref name="entityType"/>. Must depend on the type alone: the
    /// answer is cached process-wide per (policy type, entity type), so a per-request answer would be
    /// frozen at the first request's.
    /// </summary>
    bool AppliesTo(Type entityType);

    /// <summary>
    /// Whether the system context (module sync, background work with no viewer) skips this policy.
    /// Default <c>true</c>, the same exemption the Actions-class rules have. A policy that must also
    /// hide rows from background work — soft deletion — returns <c>false</c>.
    /// </summary>
    bool BypassInSystemContext => true;

    /// <summary>
    /// Whether this policy decides <b>who may see which rows</b> (ownership, tenancy) rather than
    /// hiding rows from everyone for a reason unrelated to the viewer (soft deletion, drafts).
    /// Default <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Only visibility decisions count as a row rule for the startup validator that refuses an
    /// anonymously readable type with no rule, and for the per-row <c>Can</c> flags. A soft-delete
    /// filter on every type must not silently satisfy "this anonymous type has a row policy".
    /// </remarks>
    bool IsVisibilityDecision => false;
}

/// <summary>
/// A row policy expressed as a predicate the framework composes into the database query — so paging,
/// totals and sorting stay in the database.
/// </summary>
/// <remarks>
/// <para>
/// The predicate is rebound onto the entity type <b>by member name</b> and joined to the other
/// filters with <c>AndAlso</c>: write it over an interface (<c>ISoftDeletable x =&gt; x.IsDeleted !=
/// true</c>) and every member it reads must exist, with the same type, on each entity it applies to.
/// When the query reads an index projection, the same rebinding targets the projection; a projection
/// lacking a member falls back to filtering after materialization.
/// </para>
/// <para>
/// ⚠️ A policy returns a predicate, never a query. It is not handed the <c>IQueryable</c>: RavenDB
/// groups adjacent <c>Search</c> clauses, and a security <c>Where</c> placed by a policy could end up
/// OR-ed into a search group. The framework decides where the predicate goes (before column filters
/// and search).
/// </para>
/// <para>
/// Mind absent fields: <c>x.IsDeleted != true</c> matches a document that has no <c>IsDeleted</c>
/// field; <c>!x.IsDeleted</c> and <c>x.IsDeleted == false</c> do not (measured, #460 spike S3).
/// </para>
/// </remarks>
public interface IRowFilterPolicy : IRowPolicy
{
    /// <summary>
    /// The predicate for this request and action, or null for "no restriction". A constant
    /// <c>x =&gt; false</c> hides every row; <c>x =&gt; true</c> is dropped. Called at most once per
    /// (entity type, action) per request.
    /// </summary>
    ValueTask<LambdaExpression?> GetFilterAsync(RowPolicyContext context);
}

/// <summary>
/// A row policy expressed as a per-row check over the materialized entity.
/// </summary>
/// <remarks>
/// ⚠️ Registering one switches database paging off for every type it applies to: a check that runs
/// after the database answered can drop rows from a page, so the framework must page in memory to
/// keep pages full and totals honest. Prefer <see cref="IRowFilterPolicy"/> wherever the rule can be
/// written as a predicate.
/// </remarks>
public interface IRowCheckPolicy : IRowPolicy
{
    /// <summary>Whether the caller may perform <see cref="RowPolicyContext.Action"/> on <paramref name="entity"/>.</summary>
    ValueTask<bool> IsAllowedAsync(RowPolicyContext context, object entity);
}

/// <summary>What a row policy is being asked about.</summary>
public sealed class RowPolicyContext
{
    /// <summary>The entity type whose rows are being judged.</summary>
    public required Type EntityType { get; init; }

    /// <summary>
    /// <c>"Read"</c>, <c>"Query"</c>, <c>"New"</c>, <c>"Edit"</c>, <c>"Delete"</c>, or a custom
    /// action's name.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// The request's soft-deletion mode (#460, T2). <see cref="SparkDeletedFilter.Exclude"/> unless a
    /// query request asked for more; core does not check whether the caller may — the policy that
    /// honours it does.
    /// </summary>
    public SparkDeletedFilter Deleted { get; init; }

    /// <summary>Whether the caller is the system (module sync, background work) rather than a viewer.</summary>
    public bool IsSystemContext { get; init; }

    /// <summary>The caller, when there is an HTTP request.</summary>
    public ClaimsPrincipal? User { get; init; }
}

/// <summary>
/// Typed helper for a filter policy over every entity assignable to <typeparamref name="T"/> —
/// an interface such as <c>ISoftDeletable</c> covers every implementer.
/// </summary>
public abstract class RowFilterPolicy<T> : IRowFilterPolicy where T : class
{
    /// <inheritdoc />
    public virtual bool AppliesTo(Type entityType) => typeof(T).IsAssignableFrom(entityType);

    /// <inheritdoc />
    public virtual bool BypassInSystemContext => true;

    /// <inheritdoc />
    public virtual bool IsVisibilityDecision => false;

    /// <inheritdoc cref="IRowFilterPolicy.GetFilterAsync" />
    public abstract ValueTask<Expression<Func<T, bool>>?> GetFilterAsync(RowPolicyContext context);

    async ValueTask<LambdaExpression?> IRowFilterPolicy.GetFilterAsync(RowPolicyContext context)
        => await GetFilterAsync(context);
}

/// <summary>
/// Typed helper for a per-row check policy over every entity assignable to <typeparamref name="T"/>.
/// </summary>
public abstract class RowCheckPolicy<T> : IRowCheckPolicy where T : class
{
    /// <inheritdoc />
    public virtual bool AppliesTo(Type entityType) => typeof(T).IsAssignableFrom(entityType);

    /// <inheritdoc />
    public virtual bool BypassInSystemContext => true;

    /// <inheritdoc />
    public virtual bool IsVisibilityDecision => false;

    /// <inheritdoc cref="IRowCheckPolicy.IsAllowedAsync" />
    public abstract ValueTask<bool> IsAllowedAsync(RowPolicyContext context, T entity);

    ValueTask<bool> IRowCheckPolicy.IsAllowedAsync(RowPolicyContext context, object entity)
        => entity is T typed ? IsAllowedAsync(context, typed) : ValueTask.FromResult(false);
}
