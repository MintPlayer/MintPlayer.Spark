using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark;

/// <summary>
/// The application's typed view of the store: one <see cref="IQueryable{T}"/> property per <c>Database.*</c> query source,
/// written as <c>Session.Query&lt;T&gt;()</c>. The session is the request's own, so any provider serves it (#551 M5).
/// </summary>
public abstract class SparkContext
{
    public ISparkSession Session { get; internal set; } = null!;
}
