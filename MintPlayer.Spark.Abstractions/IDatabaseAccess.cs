namespace MintPlayer.Spark.Abstractions;

public interface IDatabaseAccess
{
    Task<T> GetDocumentAsync<T>(string id) where T : class;
}
