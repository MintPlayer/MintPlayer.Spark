using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>The real <see cref="QueryResultProjector"/> for a hand-built executor or resolver: the same session, and real links.</summary>
internal static class TestQueryResultProjector
{
    public static QueryResultProjector For(IAsyncDocumentSession session, IModelLoader? modelLoader = null)
        => new(session.AsSparkSession(), new SparkEntityLinks(modelLoader ?? Substitute.For<IModelLoader>()));
}
