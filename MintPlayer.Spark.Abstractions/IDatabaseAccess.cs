namespace MintPlayer.Spark.Abstractions;

public interface IDatabaseAccess
{
    Task<T?> GetDocumentAsync<T>(string id) where T : class;
    Task<IEnumerable<T>> GetDocumentsAsync<T>() where T : class;
    Task<IEnumerable<T>> GetDocumentsByTypeAsync<T>(string clrType) where T : class;
    Task<T> SaveDocumentAsync<T>(T document) where T : class;
    Task DeleteDocumentAsync<T>(string id) where T : class;
}
