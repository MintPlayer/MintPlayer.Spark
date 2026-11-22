namespace MintPlayer.Spark.Files.FileSystem;

public static class FileSystemFilesExtensions
{
    /// <summary>
    /// Keeps file bytes in <paramref name="rootPath"/> instead of RavenDB attachments:
    /// <code>spark.AddFiles(files =&gt; files.UseFileSystem("/data/files"));</code>
    /// The directory must be private to the app (a volume, not the web root): Spark serves files only through its download
    /// endpoint, which runs the read path first.
    /// </summary>
    public static FilesBuilder UseFileSystem(this FilesBuilder files, string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var store = new FileSystemFileStore(new FileSystemFileStoreOptions { RootPath = rootPath });
        return files.UseStore(_ => store);
    }
}
