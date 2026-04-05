using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Storage;
using System.Text.Json;

namespace MintPlayer.Spark.FileSystem;

public static class FileSystemSparkExtensions
{
    /// <summary>
    /// Configures the Spark application to use the file system as the storage backend.
    /// Entities are stored as JSON files in the specified base directory.
    /// </summary>
    /// <param name="builder">The Spark builder.</param>
    /// <param name="basePath">The base directory for entity JSON files.</param>
    /// <param name="jsonOptions">Optional JSON serializer options.</param>
    public static ISparkBuilder UseFileSystem(this ISparkBuilder builder, string basePath, JsonSerializerOptions? jsonOptions = null)
    {
        builder.Services.AddSingleton<ISparkSessionFactory>(new FileSystemSessionFactory(basePath, jsonOptions));
        builder.Services.AddSingleton<ISparkStorageProvider>(new FileSystemStorageProvider());

        return builder;
    }
}
