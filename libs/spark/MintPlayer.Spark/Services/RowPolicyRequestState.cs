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
}

[Register(typeof(IRowPolicyRequestState), ServiceLifetime.Scoped)]
internal sealed partial class RowPolicyRequestState : IRowPolicyRequestState
{
    public SparkDeletedFilter Deleted { get; set; }
}
