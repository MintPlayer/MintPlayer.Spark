using MintPlayer.Spark.Storage;
using System.Text.Json;

namespace MintPlayer.Spark.FileSystem;

/// <summary>
/// File-based implementation of <see cref="ISparkSessionFactory"/>.
/// Creates sessions that read/write JSON files from a base directory.
/// </summary>
public class FileSystemSessionFactory : ISparkSessionFactory
{
    private readonly string _basePath;
    private readonly JsonSerializerOptions? _jsonOptions;

    public FileSystemSessionFactory(string basePath, JsonSerializerOptions? jsonOptions = null)
    {
        _basePath = basePath;
        _jsonOptions = jsonOptions;
        Directory.CreateDirectory(basePath);
    }

    public ISparkSession OpenSession()
    {
        return new FileSystemSparkSession(_basePath, _jsonOptions);
    }
}
