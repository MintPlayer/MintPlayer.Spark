using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services.Breadcrumb;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Everything the gate needs to judge one set of rows. One object, so a new dimension is added in
/// one place rather than as a seventh positional argument at four call sites.
/// </summary>
internal sealed record RowSecurityContext
{
    /// <summary>
    /// The session the gate's own reads run on — a projection reload, the breadcrumb pass,
    /// redaction's document reload.
    /// </summary>
    /// <remarks>
    /// Per invocation, deliberately NOT taken from DI. Streaming opens a fresh session per batch so
    /// its request count starts from zero each time; taking the request session here would silently
    /// undo that and reintroduce the failure where a long stream over a referenced type died partway
    /// through.
    /// </remarks>
    public required IAsyncDocumentSession Session { get; init; }

    /// <summary>The model type the rows are mapped against.</summary>
    public required EntityTypeDefinition Definition { get; init; }

    /// <summary>
    /// The CLR type the row rule is written against, or <see langword="null"/> for a composed type
    /// whose rows are computed rather than stored.
    /// </summary>
    public required Type? EntityType { get; init; }

    /// <summary>
    /// The materialized row type: an index projection when one is in play, otherwise the same as
    /// <see cref="EntityType"/>. Row security reloads base documents when they differ.
    /// </summary>
    public required Type? ResultType { get; init; }

    /// <summary>The verb the rule is asked about: <c>Query</c> on list paths, <c>Read</c> on detail.</summary>
    public required string Action { get; init; }

    /// <summary>
    /// Collapse rows sharing an id. True only where a RavenDB fan-out index can emit several entries
    /// per document; <b>destructive</b> in memory, where every null id compares equal and the whole
    /// result would collapse to one row.
    /// </summary>
    public bool DedupeById { get; init; }

    /// <summary>
    /// Ordering to apply <em>after</em> redaction, when the caller could not push it into the query.
    /// </summary>
    /// <remarks>
    /// Passed in rather than applied by the caller so the ordering constraint is structural: sorting
    /// before redaction orders rows by a value the caller may not read, which is the same comparison
    /// oracle the sortable-column gate exists to close. By the time the gate sorts, a protected value
    /// is already null.
    /// </remarks>
    public Func<IEnumerable<PersistentObject>, IEnumerable<PersistentObject>>? OrderRows { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>How a set of rows came to be allowed out.</summary>
internal enum RowSecurityMode
{
    /// <summary>The framework judged every row: the filter ran and redaction ran.</summary>
    Enforced,

    /// <summary>
    /// The type declared that its actions class owns row security, because there is no stored
    /// document to judge. Deliberate, and recorded — not the same as nobody having decided.
    /// </summary>
    DelegatedToActions,
}

/// <summary>
/// The single place row security is applied to a set of rows before they can become a result.
/// </summary>
/// <remarks>
/// <para>
/// The sequence — filter, breadcrumb, map, redact — was hand-assembled at four call sites, which is
/// four chances to drop a line and no way to notice. Worse, the two most complex of those call sites
/// were covered only by suites installing a permissive row-security double, under which "allow
/// everything" and "never asked" produce identical rows.
/// </para>
/// <para>
/// So the output type is the enforcement. <see cref="SecuredRows"/> has a private constructor and is
/// nested here, which by C# accessibility means no other type in this assembly can create one — and
/// every framework shape that carries rows to the wire demands one. A new row-returning path cannot
/// skip the gate without deleting a parameter, which is a signature change rather than a missing
/// line.
/// </para>
/// </remarks>
internal interface IRowSecurityGate
{
    /// <summary>
    /// Filters, resolves breadcrumbs for, maps and redacts <paramref name="rows"/>, in that fixed
    /// order.
    /// </summary>
    Task<RowSecurityGate.SecuredRows> ApplyAsync(IReadOnlyList<object> rows, RowSecurityContext context);
}

[Register(typeof(IRowSecurityGate), ServiceLifetime.Scoped)]
internal sealed partial class RowSecurityGate : IRowSecurityGate
{
    [Inject] private readonly IRowSecurity rowSecurity;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IBreadcrumbResolver breadcrumbResolver;

    /// <summary>
    /// Rows that have been through the gate, and the only thing the result shapes accept.
    /// </summary>
    /// <remarks>
    /// A sealed <b>class</b>, not a struct, and that is the whole design rather than a style
    /// preference: <c>default(T)</c> on a struct would be a free forged token, and closing that hole
    /// would need a runtime null-check — turning a compile-time guarantee back into a runtime one.
    /// On a reference type <c>default</c> is <see langword="null"/>, which the non-nullable
    /// parameters reject at the call site.
    /// </remarks>
    internal sealed class SecuredRows
    {
        private SecuredRows(IReadOnlyList<PersistentObject> rows, RowSecurityMode mode)
            => (Rows, Mode) = (rows, mode);

        /// <summary>The rows that survived, mapped and redacted.</summary>
        public IReadOnlyList<PersistentObject> Rows { get; }

        /// <summary>Whether the framework judged these rows or the type's actions class owns that.</summary>
        public RowSecurityMode Mode { get; }

        public int Count => Rows.Count;

        internal static SecuredRows Create(IReadOnlyList<PersistentObject> rows, RowSecurityMode mode)
            => new(rows, mode);

        /// <summary>
        /// Rows already judged by the <b>per-row</b> gate on the detail load path, rather than by
        /// this gate's per-set pass.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The one crack in an otherwise sealed type, and it is deliberate, narrow and temporary.
        /// <c>DefaultPersistentObjectActions.LoadManyAsync</c> enforces row security per row — the
        /// collection guard, then <c>IsAllowedAsync(Read, entity)</c>, then redaction — and returns
        /// <see cref="PersistentObject"/>s that are already mapped. So its rows genuinely are
        /// enforced; they simply arrive from a different enforcement point, one that predates this
        /// gate and does not mint a token.
        /// </para>
        /// <para>
        /// <b>It is not a general escape hatch.</b> There is exactly one caller: the custom-action
        /// selection fallback, which materialises a selection by loading documents by id when the
        /// originating query cannot be re-run. It disappears when <c>LoadManyAsync</c> itself moves
        /// onto this gate, at which point that path will hold a real token and this method should be
        /// deleted rather than kept for convenience.
        /// </para>
        /// </remarks>
        /// <param name="rows">Rows produced by the row-gated batched load.</param>
        internal static SecuredRows FromRowGatedLoad(IReadOnlyList<PersistentObject> rows)
            => new(rows, RowSecurityMode.Enforced);

        /// <summary>
        /// A subset of these rows, still secured. For the work that legitimately happens after the
        /// gate: the in-memory search fallback, the count, and paging.
        /// </summary>
        /// <remarks>
        /// Narrowing is the only transformation that cannot break the invariant. The gate is the sole
        /// way to <em>create</em> a token; this is the sole way to change one, and it can only ever
        /// remove rows from a set that already passed. A caller cannot smuggle an unjudged row in
        /// through here, because it has nothing to put in — it starts from rows that are already
        /// through.
        /// <para>
        /// It is an instance method for that reason: you must already hold a secured set to produce
        /// another one.
        /// </para>
        /// </remarks>
        public SecuredRows Narrow(Func<IEnumerable<PersistentObject>, IEnumerable<PersistentObject>> narrow)
            => new([.. narrow(Rows)], Mode);
    }

    public async Task<SecuredRows> ApplyAsync(IReadOnlyList<object> rows, RowSecurityContext context)
    {
        var mode = context.EntityType is null ? RowSecurityMode.DelegatedToActions : RowSecurityMode.Enforced;

        // A composed row is computed, not stored: there is no document to re-judge and no stored
        // value to compare a mapped attribute against, so neither half can run. The type has already
        // had to declare that its actions class owns the job — see the check in ExecuteCustomQueryAsync
        // — so reaching here in DelegatedToActions mode is a stated position rather than an omission.
        var kept = mode is RowSecurityMode.Enforced
            ? await rowSecurity.FilterAsync(
                context.Session, rows, context.EntityType!, context.ResultType!, context.Action, context.CancellationToken)
            : rows;

        var breadcrumbs = await breadcrumbResolver.ResolveAsync(
            context.Session, kept, context.Definition, context.CancellationToken);

        var mapped = kept
            .Select(e => (Po: entityMapper.ToPersistentObject(e, context.Definition.Id, breadcrumbs), Row: e))
            .ToList();

        if (mode is RowSecurityMode.Enforced)
        {
            await rowSecurity.RedactAsync(
                context.Session, mapped, context.EntityType!, context.ResultType!, context.Action, context.CancellationToken);
        }

        IEnumerable<PersistentObject> result = mapped.Select(m => m.Po);

        if (context.DedupeById)
            result = result.DistinctBy(po => po.Id);

        // After redaction, always. See RowSecurityContext.OrderRows.
        if (context.OrderRows is not null)
            result = context.OrderRows(result);

        return SecuredRows.Create([.. result], mode);
    }
}
