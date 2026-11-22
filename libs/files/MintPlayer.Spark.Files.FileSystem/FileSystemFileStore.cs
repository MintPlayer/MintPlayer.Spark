using System.Runtime.CompilerServices;
using MintPlayer.Spark.Files.Storage;

namespace MintPlayer.Spark.Files.FileSystem;

/// <summary>Where <see cref="FileSystemFileStore"/> keeps its files.</summary>
public sealed class FileSystemFileStoreOptions
{
    /// <summary>The directory, typically a mounted volume. Created when missing. Required.</summary>
    public string RootPath { get; set; } = string.Empty;
}

/// <summary>
/// File bytes in a local directory (#700 D12). A key maps to a path under <see cref="FileSystemFileStoreOptions.RootPath"/>,
/// and only after <see cref="ObjectFileStore"/> validated it and the resolved full path is confirmed inside the root. A write
/// goes to a temporary file in the target directory and is moved into place, so a reader never sees half a file. Bytes go
/// through Spark: there is no direct upload target.
/// </summary>
public sealed class FileSystemFileStore : ObjectFileStore
{
    private const string TempPrefix = ".spark-";
    private readonly string root;

    public FileSystemFileStore(FileSystemFileStoreOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath))
            throw new ArgumentException("The file system store needs a RootPath.", nameof(options));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath)) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
    }

    public override Task<SparkFileUploadTarget?> CreateUploadTargetAsync(string key, long maxBytes, DateTimeOffset expires, CancellationToken cancellationToken)
        => Task.FromResult<SparkFileUploadTarget?>(null);

    public override async Task WriteAsync(string key, Stream content, string? contentType, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, TempPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await content.CopyToAsync(file, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public override Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        try
        {
            return Task.FromResult<Stream?>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public override Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathOf(key);
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
            return Task.CompletedTask;
        File.Delete(path);
        // An entity's directory goes with its last file.
        if (key.StartsWith(EntitiesRoot, StringComparison.Ordinal) && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            try { Directory.Delete(directory); }
            catch (IOException) { /* a write raced in: the directory is in use again */ }
        }
        return Task.CompletedTask;
    }

    public override async Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken)
    {
        await using var source = await OpenReadAsync(sourceKey, cancellationToken)
            ?? throw new FileNotFoundException("The source file is gone.", sourceKey);
        await WriteAsync(destinationKey, source, contentType: null, cancellationToken);
    }

    public override async IAsyncEnumerable<SparkStoredObject> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var directory = DirectoryOf(EnsurePrefix(prefix));
        if (!Directory.Exists(directory))
            yield break;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(path).StartsWith(TempPrefix, StringComparison.Ordinal))
                continue;
            var key = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            yield return new SparkStoredObject(key, new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
        }
        await Task.CompletedTask;
    }

    private string PathOf(string key) => Inside(Path.Combine(root, EnsureKey(key).Replace('/', Path.DirectorySeparatorChar)));

    private string DirectoryOf(string prefix) => Inside(Path.Combine(root, prefix.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Defence in depth behind the key pattern: the resolved path must sit inside the root.</summary>
    private string Inside(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? full
            : throw new ArgumentException("The key leaves the store's root.");
    }
}
