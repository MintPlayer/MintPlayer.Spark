using Raven.Client.Documents.Session;

namespace MintPlayer.Spark;

public abstract class SparkContext
{
    public IAsyncDocumentSession Session { get; internal set; } = null!;
}
