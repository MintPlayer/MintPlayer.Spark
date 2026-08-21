using System.Linq.Expressions;

namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// The row-level rule an entity's Actions class declares, made available to code outside Spark's own
/// pipeline — a controller, a background job, a report.
/// <para>
/// Before this existed the rule was reachable only by going through <c>/spark</c>, so an application
/// with a mixed <c>/spark</c> + <c>/api</c> surface wrote the same predicate two or three times and
/// kept the copies in step by hand (#301). The residual cost is not the predicate — an application
/// can centralise that itself — it is the <em>plumbing</em>: knowing the rule must be applied,
/// remembering there is a second half, and keeping the expression form and the compiled form in
/// agreement.
/// </para>
/// <para>
/// <b>Scope.</b> This governs which <em>rows</em> a caller may see. It does not redact
/// <em>attributes</em> of an application's own DTOs — <c>GetProtectedAttributesAsync</c> reports what
/// must be hidden, but applying it to a shape Spark did not map is the caller's job. Nothing here
/// makes an arbitrary API endpoint safe on its own.
/// </para>
/// </summary>
/// <typeparam name="T">The entity type the rule is written against — the stored document, not a projection.</typeparam>
public interface ISparkRowRule<T> where T : class
{
    /// <summary>
    /// Applies the complete rule to <paramref name="query"/> and returns the rows this caller may
    /// see: the filter is pushed into the database where it is translatable, and what comes back is
    /// then narrowed by the compiled predicate <b>and</b> the per-row hook.
    /// <para>
    /// This is the method to use. <see cref="GetFilterAsync"/> is half the rule, and the half that is
    /// missing depends on which hooks the Actions class happens to override — so a caller composing
    /// the filter itself is correct until someone adds an <c>IsAllowedAsync</c> override, at which
    /// point it silently stops filtering. One call cannot forget the other half.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<T>> ApplyAsync(IQueryable<T> query, string action, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same, for a query that projects — a static index, a <c>[FromIndex]</c> view type.
    /// <para>
    /// The rule is written against the document, so the filter cannot compose into a projection
    /// query and the documents behind the surviving rows are batch-loaded and judged instead. That
    /// is a real cost on a large collection, and it is the only correct answer: deciding ownership
    /// from a partial view is how a filter silently passes everything. A projected row must carry an
    /// <c>Id</c>, or nothing can be correlated back and <b>no rows</b> are returned — unverifiable is
    /// not shown.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<TResult>> ApplyAsync<TResult>(
        IQueryable<TResult> query, string action, CancellationToken cancellationToken = default)
        where TResult : class;

    /// <summary>
    /// Whether this caller may perform <paramref name="action"/> on one already-loaded row. The
    /// single-row form of the same rule, derived from the same expression, so a detail check and a
    /// list cannot disagree.
    /// </summary>
    Task<bool> IsAllowedAsync(string action, T entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// The attributes of this row the caller must not see, or <see langword="null"/> when there are
    /// none. Attribute visibility, not row visibility — see the note on the interface.
    /// </summary>
    Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(
        string action, T entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// The raw predicate, for callers that must compose it themselves — paging over a count,
    /// aggregation, faceting.
    /// <para>
    /// <b><see langword="null"/> means unrestricted.</b> Never coalesce it to <c>x =&gt; false</c>:
    /// that inverts the rule. And <see langword="null"/> does not mean "this type has no rule" — a
    /// type that expresses its policy through <c>IsAllowedAsync</c> alone returns
    /// <see langword="null"/> here and is not unrestricted at all. Use <see cref="ApplyAsync(IQueryable{T}, string, CancellationToken)"/>
    /// unless the shape genuinely cannot.
    /// </para>
    /// </summary>
    Task<Expression<Func<T, bool>>?> GetFilterAsync(string action, CancellationToken cancellationToken = default);
}
