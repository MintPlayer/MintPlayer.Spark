using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Actions;

/// <summary>
/// What <see cref="DefaultPersistentObjectActions{T}.OnNewAsync"/> is handed: a freshly scaffolded
/// object that has never been persisted, plus the context needed to decide what it should start out
/// looking like.
/// <para>
/// Construction is not persistence. Nothing here is written; the object is handed back to the
/// client to render, and for an <c>AsDetail</c> row the parent still owns the save. A hook that
/// wants to write should do it in <see cref="IPersistentObjectActions{T}.OnBeforeSaveAsync"/>.
/// </para>
/// </summary>
/// <typeparam name="T">The entity type the actions class serves.</typeparam>
public sealed class SparkNewArgs<T> where T : class
{
    internal SparkNewArgs(
        PersistentObject persistentObject,
        PersistentObject? parent,
        PersistentObject? asDetailParent,
        string? asDetailAttribute,
        IReadOnlyDictionary<string, string>? parameters,
        SparkNewSubQueryContext? subQuery,
        CancellationToken cancellationToken)
    {
        PersistentObject = persistentObject;
        Parent = parent;
        AsDetailParent = asDetailParent;
        AsDetailAttribute = asDetailAttribute;
        Parameters = parameters ?? EmptyParameters;
        this.subQuery = subQuery;
        CancellationToken = cancellationToken;
    }

    private readonly SparkNewSubQueryContext? subQuery;

    /// <summary>
    /// The parent's entity type when New was started from a sub-query on the parent's detail page
    /// (#460, D19); <see langword="null"/> otherwise — a standalone New or an <c>AsDetail</c> row.
    /// </summary>
    public EntityTypeDefinition? ParentType => subQuery?.ParentType;

    /// <summary>
    /// The sub-query New was started from (the parent's <c>Queries</c> entry resolved to its query);
    /// <see langword="null"/> when New was not started from a sub-query.
    /// </summary>
    public Queries.SparkQueryInfo? Query => subQuery?.Query;

    /// <summary>
    /// The attribute the sub-query entry or its query names as the parent reference
    /// (<c>parentReference</c>), validated at startup; <see langword="null"/> when none is named and the
    /// framework looks for the single reference to the parent's type.
    /// </summary>
    public string? ParentReference => subQuery?.ParentReference;

    /// <summary>
    /// Sets the new object's reference to <see cref="Parent"/> when New was started from a sub-query —
    /// what the base <c>OnNewAsync</c> does (#460, D19, owner refinement), exposed so an override that
    /// does not call base can still ask for it.
    /// </summary>
    /// <remarks>
    /// The attribute is <see cref="ParentReference"/> when one is named; otherwise the one
    /// <c>Reference</c> attribute of this type whose target is the parent's type. Zero or several
    /// candidates do nothing (logged at Debug) — guessing between two references to the same type is
    /// how a row ends up under the wrong parent. The value is set as the attribute's original value,
    /// with the parent's breadcrumb, so the object does not start dirty and the picker shows a label.
    /// Never applies to an <c>AsDetail</c> row, whose parent owns the save.
    /// </remarks>
    /// <returns>The name of the attribute that was filled, or <see langword="null"/>.</returns>
    public string? FillParentReference()
    {
        if (subQuery is null || Parent?.Id is not { Length: > 0 } parentId || AsDetailParent is not null)
            return null;

        var parentClrType = subQuery.ParentType.ClrType;
        string? name = subQuery.ParentReference;
        if (name is null)
        {
            var candidates = subQuery.OwnType.Attributes
                .Where(a => a.DataType == "Reference" && !a.IsArray
                    && parentClrType is not null
                    && string.Equals(a.ReferenceType, parentClrType, StringComparison.Ordinal))
                .Select(a => a.Name)
                .ToArray();

            if (candidates.Length != 1)
            {
                subQuery.Logger?.LogDebug(
                    "New '{Type}' from sub-query '{Query}': {Count} reference attribute(s) target '{ParentType}', so none "
                    + "was filled. Name one with 'parentReference' on the query or the sub-query entry.",
                    subQuery.OwnType.Name, subQuery.Query.Name, candidates.Length, subQuery.ParentType.Name);
                return null;
            }

            name = candidates[0];
        }

        var attribute = PersistentObject.Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
        if (attribute is null)
            return null;

        attribute.SetOriginalValue(parentId);
        attribute.Breadcrumb = Parent.Breadcrumb ?? Parent.Name;
        return name;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
        new Dictionary<string, string>(0);

    /// <summary>
    /// The object being constructed. Every attribute the model declares is present with a null
    /// value; set defaults with
    /// <see cref="PersistentObjectAttribute.SetOriginalValue{T}"/> so the object does not start
    /// dirty.
    /// </summary>
    public PersistentObject PersistentObject { get; }

    /// <summary>
    /// The object this one is being created from, or <see langword="null"/> for a standalone New.
    /// </summary>
    /// <remarks>
    /// Get-only, deliberately. Spark does not bind a child's parent-typed attribute before this hook
    /// runs — the base hook does it, for a sub-query New only, through
    /// <see cref="FillParentReference"/> (#460, D19) — so there is nothing for a hook to redirect: a
    /// hook that wants a different object — the aggregate root two levels up, for a grandchild
    /// collection — simply sets the attribute it wants from the object it wants, and skips base.
    /// <para>
    /// If automatic binding is ever added, the redirect must be an explicit argument or a named
    /// method, <b>never</b> a setter here. In the prior art the substituting hook goes on to read
    /// the <em>original</em> parent after delegating with a substitute, so both values have to stay
    /// reachable at once; a setter silently destroys the original unless every author remembers to
    /// stash it first.
    /// </para>
    /// </remarks>
    public PersistentObject? Parent { get; }

    /// <summary>
    /// The object whose <c>AsDetail</c> collection this row is being added to; <see langword="null"/>
    /// for anything that is not an embedded row.
    /// <para>
    /// The narrow counterpart to <see cref="Parent"/>, and the one that answers "who owns the
    /// save". While it is non-null the child is part of its parent's aggregate and must not persist
    /// itself.
    /// </para>
    /// </summary>
    public PersistentObject? AsDetailParent { get; }

    /// <summary>
    /// The name of the <c>AsDetail</c> attribute the row is being added to, or
    /// <see langword="null"/> when this is not an embedded row.
    /// <para>
    /// Present because one child type can appear in more than one collection on the same parent and
    /// want different defaults in each — without this, a hook cannot tell which Add button was
    /// pressed.
    /// </para>
    /// </summary>
    public string? AsDetailAttribute { get; }

    /// <summary>
    /// Free-form arguments from the client. Carries the chosen variant when New is a menu rather
    /// than a single button.
    /// </summary>
    /// <remarks>
    /// Never <see langword="null"/> — an empty dictionary when the client sent nothing, matching
    /// what the prior art hands its construction hook. A hook that reads a parameter should not
    /// have to null-check first.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>Cancelled when the caller gives up on the request.</summary>
    public CancellationToken CancellationToken { get; }
}

