using MintPlayer.Spark.Storage;

namespace MintPlayer.Spark;

public abstract class SparkContext
{
    public ISparkSession Session { get; internal set; } = null!;
}
