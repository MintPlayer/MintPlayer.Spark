using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Actions;

/// <summary>
/// Where a New started from a sub-query came from (#460, D19): the parent's type, the sub-query, and
/// the parent reference it names. Built by the framework only; a hook reads it through
/// <see cref="SparkNewArgs{T}.ParentType"/>, <see cref="SparkNewArgs{T}.Query"/> and
/// <see cref="SparkNewArgs{T}.ParentReference"/>.
/// </summary>
public sealed class SparkNewSubQueryContext
{
    internal SparkNewSubQueryContext(
        EntityTypeDefinition ownType,
        EntityTypeDefinition parentType,
        SparkQuery query,
        string? parentReference,
        ILogger? logger)
    {
        OwnType = ownType;
        ParentType = parentType;
        Query = Queries.SparkQueryInfo.From(query);
        ParentReference = parentReference;
        Logger = logger;
    }

    internal EntityTypeDefinition OwnType { get; }
    internal EntityTypeDefinition ParentType { get; }
    internal Queries.SparkQueryInfo Query { get; }
    internal string? ParentReference { get; }
    internal ILogger? Logger { get; }
}
