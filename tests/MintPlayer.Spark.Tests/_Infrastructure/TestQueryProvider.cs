using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage.Raven;
using NSubstitute;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>The RavenDB query provider for a hand-built <c>QueryExecutor</c>, over the executor's own session.</summary>
internal static class TestQueryProvider
{
    public static ISparkQueryProvider For(ISparkSession session, IIndexCatalog? indexCatalog = null)
        => new RavenSparkQueryProvider(session, indexCatalog ?? Substitute.For<IIndexCatalog>());
}
