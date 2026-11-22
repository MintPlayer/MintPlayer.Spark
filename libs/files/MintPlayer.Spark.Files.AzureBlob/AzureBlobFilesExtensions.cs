using Azure.Storage.Blobs;

namespace MintPlayer.Spark.Files.AzureBlob;

public static class AzureBlobFilesExtensions
{
    /// <summary>
    /// Keeps file bytes in an Azure Blob Storage container instead of RavenDB attachments:
    /// <code>spark.AddFiles(files =&gt; files.UseAzureBlob(connectionString, "files"));</code>
    /// The container is created when missing, private.
    /// </summary>
    public static FilesBuilder UseAzureBlob(this FilesBuilder files, string connectionString, string containerName, Action<AzureBlobFileStoreOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        return files.UseAzureBlob(new BlobContainerClient(connectionString, containerName), configure);
    }

    /// <summary>
    /// The same, with a container client you build (a managed identity's <c>TokenCredential</c>, for instance). A client that
    /// can't sign a SAS sends bytes through Spark.
    /// </summary>
    public static FilesBuilder UseAzureBlob(this FilesBuilder files, BlobContainerClient container, Action<AzureBlobFileStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        var options = new AzureBlobFileStoreOptions();
        configure?.Invoke(options);
        var store = new AzureBlobFileStore(container, options);
        // Once, when the store is first resolved: the container exists before the first upload.
        return files.UseStore(_ =>
        {
            container.CreateIfNotExists();
            return store;
        });
    }
}
