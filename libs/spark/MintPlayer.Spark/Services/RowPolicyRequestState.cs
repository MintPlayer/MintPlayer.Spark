using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The request flags row policies see through <see cref="Abstractions.Authorization.RowPolicyContext"/>
/// (#460, T2). Set by the query endpoints from the request body, before anything asks row security.
/// </summary>
internal interface IRowPolicyRequestState
{
    /// <summary>The request's <c>deleted</c> mode. <see cref="SparkDeletedFilter.Exclude"/> unless a query asked otherwise.</summary>
    SparkDeletedFilter Deleted { get; set; }

    /// <summary>
    /// The CLR type (full name) the <see cref="Deleted"/> mode was asked for — the object opened from
    /// the recycle bin, or the query's entity type. Every other type in the same request (a
    /// reference's display value, the breadcrumb of a referenced row) sees
    /// <see cref="SparkDeletedFilter.Exclude"/>: a deleted answer's live question must not vanish
    /// because the answer was opened with <c>deleted: only</c>. <see langword="null"/> applies the
    /// mode to every type, for a caller that does not know the type.
    /// </summary>
    string? DeletedScopeClrType { get; set; }

    /// <summary>The <c>deleted</c> mode row policies see for <paramref name="entityType"/>.</summary>
    SparkDeletedFilter DeletedFor(Type entityType)
        => DeletedScopeClrType is null || string.Equals(DeletedScopeClrType, entityType.FullName, StringComparison.Ordinal)
            ? Deleted
            : SparkDeletedFilter.Exclude;
}

[Register(typeof(IRowPolicyRequestState), ServiceLifetime.Scoped)]
internal sealed partial class RowPolicyRequestState : IRowPolicyRequestState
{
    public SparkDeletedFilter Deleted { get; set; }

    public string? DeletedScopeClrType { get; set; }
}
