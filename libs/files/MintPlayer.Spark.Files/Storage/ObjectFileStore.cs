using System.Text;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Files.Storage;

/// <summary>
/// The base of a store that keeps bytes as named objects outside RavenDB (a directory, a blob container, a bucket). It owns
/// the key layout, so every such store names, validates and reconciles alike:
/// <list type="bullet">
/// <item><c>staging/{token}</c>: an upload's bytes until a save claims it, deleted after the claiming save commits; its image
/// variants are <c>staging/{token}.{variant}</c>.</item>
/// <item><c>entities/{base64url(entityId)}/{fileId}</c>: a file an entity holds; image variants (M8) add a <c>.{variant}</c>
/// suffix. The entity id is encoded, so no id (with its slashes) can climb out of its own prefix.</item>
/// </list>
/// Every key and prefix that reaches a store passes <see cref="EnsureKey"/>/<see cref="EnsurePrefix"/>: a key this layout did
/// not produce is refused before any I/O.
/// </summary>
public abstract partial class ObjectFileStore : IFileStore
{
    public const string StagingRoot = "staging/";
    public const string EntitiesRoot = "entities/";

    [GeneratedRegex("^(staging/[A-Za-z0-9_-]{1,64}(\\.[A-Za-z0-9_-]{1,64})?|entities/[A-Za-z0-9_-]{1,1024}/[A-Za-z0-9][A-Za-z0-9._-]{0,127})$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^(staging/|entities/|entities/[A-Za-z0-9_-]{1,1024}/)$")]
    private static partial Regex PrefixPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex FileIdPattern();

    public abstract Task<SparkFileUploadTarget?> CreateUploadTargetAsync(string key, long maxBytes, DateTimeOffset expires, CancellationToken cancellationToken);

    public abstract Task WriteAsync(string key, Stream content, string? contentType, CancellationToken cancellationToken);

    public abstract Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>No direct reads by default: the bytes are proxied through Spark.</summary>
    public virtual Uri? CreateReadUrl(string key, SparkFileReadRequest request) => null;

    public abstract Task DeleteAsync(string key, CancellationToken cancellationToken);

    public abstract Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken);

    public abstract IAsyncEnumerable<SparkStoredObject> ListAsync(string prefix, CancellationToken cancellationToken);

    public string StagingKey(string token)
        => TokenPattern().IsMatch(token) ? StagingRoot + token : throw new ArgumentException("Not an upload token.", nameof(token));

    public string StagingVariantKey(string token, string variant)
        => SparkFileKeys.IsVariantName(variant) ? StagingKey(token) + "." + variant : throw new ArgumentException("Not a variant name.", nameof(variant));

    public string EntityKey(string entityId, string fileId)
        => FileIdPattern().IsMatch(fileId) ? EntityPrefix(entityId) + fileId : throw new ArgumentException("Not a file id.", nameof(fileId));

    public string EntityPrefix(string entityId)
    {
        ArgumentException.ThrowIfNullOrEmpty(entityId);
        return EntitiesRoot + Base64Url(Encoding.UTF8.GetBytes(entityId)) + "/";
    }

    /// <summary>Deletes the file and every variant of it; the store keeps no other copy.</summary>
    public virtual async Task EraseAsync(string entityId, string fileId, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        await foreach (var item in ListAsync(EntityPrefix(entityId), cancellationToken))
        {
            if (TryParseEntity(item.Key, out _, out var id) && id == fileId)
                keys.Add(item.Key);
        }
        foreach (var key in keys)
            await DeleteAsync(key, cancellationToken);
    }

    /// <summary>Refuses a key this layout did not produce.</summary>
    protected static string EnsureKey(string key)
        => key is not null && KeyPattern().IsMatch(key) ? key : throw new ArgumentException("Not a key of this file store.", nameof(key));

    /// <summary>Refuses a prefix this layout did not produce.</summary>
    protected static string EnsurePrefix(string prefix)
        => prefix is not null && PrefixPattern().IsMatch(prefix) ? prefix : throw new ArgumentException("Not a key prefix of this file store.", nameof(prefix));

    /// <summary>Whether <paramref name="key"/> is a staged upload's (or one of its variants), and its token.</summary>
    public static bool TryParseStaging(string key, out string token)
    {
        token = string.Empty;
        if (!key.StartsWith(StagingRoot, StringComparison.Ordinal) || !KeyPattern().IsMatch(key))
            return false;
        var name = key[StagingRoot.Length..];
        var dot = name.IndexOf('.');
        token = dot < 0 ? name : name[..dot];
        return true;
    }

    /// <summary>Whether <paramref name="key"/> is an entity's file (or variant), and whose: the variant suffix is not part of <paramref name="fileId"/>.</summary>
    public static bool TryParseEntity(string key, out string entityId, out string fileId)
    {
        entityId = fileId = string.Empty;
        if (!key.StartsWith(EntitiesRoot, StringComparison.Ordinal) || !KeyPattern().IsMatch(key))
            return false;
        var rest = key[EntitiesRoot.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || !TryFromBase64Url(rest[..slash], out var id))
            return false;
        var name = rest[(slash + 1)..];
        var dot = name.IndexOf('.');
        (entityId, fileId) = (id, dot < 0 ? name : name[..dot]);
        return true;
    }

    /// <summary>
    /// <paramref name="content"/> when it can seek, else a copy in a temporary file that deletes itself on dispose: SDKs that
    /// must know the length (or replay the body) refuse a forward-only request body. Disposing the result never disposes
    /// <paramref name="content"/>.
    /// </summary>
    protected static Task<Stream> SeekableAsync(Stream content, CancellationToken cancellationToken)
        => SeekableSpool.SpoolAsync(content, cancellationToken);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string text, out string value)
    {
        value = string.Empty;
        var base64 = text.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        var buffer = new byte[base64.Length];
        if (!Convert.TryFromBase64String(base64, buffer, out var written))
            return false;
        value = Encoding.UTF8.GetString(buffer, 0, written);
        return value.Length > 0;
    }
}

/// <summary>See <see cref="ObjectFileStore.SeekableAsync"/>.</summary>
internal static class SeekableSpool
{
    public static async Task<Stream> SpoolAsync(Stream content, CancellationToken cancellationToken)
    {
        if (content.CanSeek)
            return new NonClosingStream(content);
        var path = Path.Combine(Path.GetTempPath(), "spark-files-" + Guid.NewGuid().ToString("N"));
        var spool = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await content.CopyToAsync(spool, cancellationToken);
            spool.Position = 0;
            return spool;
        }
        catch
        {
            await spool.DisposeAsync();
            throw;
        }
    }

    /// <summary>The caller's seekable stream, which the caller still owns.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
