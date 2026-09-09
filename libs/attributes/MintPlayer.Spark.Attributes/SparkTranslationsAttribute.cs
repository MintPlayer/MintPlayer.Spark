namespace MintPlayer.Spark.Abstractions;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class SparkTranslationsAttribute : Attribute
{
    public SparkTranslationsAttribute(int chunkIndex, int chunkCount, string json)
    {
        ChunkIndex = chunkIndex;
        ChunkCount = chunkCount;
        Json = json;
    }

    public int ChunkIndex { get; }
    public int ChunkCount { get; }
    public string Json { get; }
}
