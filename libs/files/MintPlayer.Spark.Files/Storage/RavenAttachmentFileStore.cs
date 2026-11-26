using System.Text.RegularExpressions;
using Raven.Client.Documents;
using Raven.Client.Documents.Attachments;
using Raven.Client.Documents.Operations.Attachments;
using Raven.Client.Documents.Commands;
using Raven.Client.Exceptions.Documents;
using MintPlayer.Spark.Files.Staging;
using Sparrow.Json;

namespace MintPlayer.Spark.Files.Storage;

/// <summary>
/// The default store: RavenDB attachments (#700 D12). A key is <c>{documentId}/{attachmentName}</c>: staged bytes hang
/// off their <c>SparkFileUploads/</c> document (and expire with it), stored bytes off the entity's own document (and are
/// deleted with it). Bytes are proxied through Spark, so there is no direct upload target.
/// </summary>
public sealed partial class RavenAttachmentFileStore(IDocumentStore store) : ISessionFileStore
{
    private const string OriginalName = "original";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex AttachmentName();

    public Task<SparkFileUploadTarget?> CreateUploadTargetAsync(string key, long maxBytes, DateTimeOffset expires, CancellationToken cancellationToken)
        => Task.FromResult<SparkFileUploadTarget?>(null);

    public async Task WriteAsync(string key, Stream content, string? contentType, CancellationToken cancellationToken)
    {
        var (documentId, name) = Split(key);
        // RavenDB refuses a forward-only stream (it may replay the body on failover). A request body is forward-only, so it is
        // spooled to a temporary file first: bounded by the caller's length limit, never held in memory, gone on dispose.
        await using var seekable = await SeekableSpool.SpoolAsync(content, cancellationToken);
        await store.Operations.SendAsync(new PutAttachmentOperation(documentId, name, seekable, contentType), token: cancellationToken);
    }

    /// <summary>Attachments are proxied through Spark.</summary>
    public Uri? CreateReadUrl(string key, SparkFileReadRequest request) => null;

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var (documentId, name) = Split(key);
        AttachmentResult? result;
        try
        {
            result = await store.Operations.SendAsync(new GetAttachmentOperation(documentId, name, AttachmentType.Document, changeVector: null), token: cancellationToken);
        }
        catch (DocumentDoesNotExistException)
        {
            return null;
        }
        return result is null ? null : new OwningStream(result.Stream, result);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var (documentId, name) = Split(key);
        try
        {
            await store.Operations.SendAsync(new DeleteAttachmentOperation(documentId, name), token: cancellationToken);
        }
        catch (DocumentDoesNotExistException)
        {
            // Gone with its document: nothing to delete.
        }
    }

    public async Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken)
    {
        var (sourceDocument, sourceName) = Split(sourceKey);
        var (destinationDocument, destinationName) = Split(destinationKey);
        using var session = store.OpenAsyncSession();
        session.Advanced.Attachments.Copy(sourceDocument, sourceName, destinationDocument, destinationName);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The attachments of one document: <paramref name="prefix"/> is <c>{documentId}/</c>, optionally followed by the start of
    /// an attachment name. An attachment has no date of its own; it reports its document's last modification.
    /// </summary>
    public async IAsyncEnumerable<SparkStoredObject> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var slash = prefix.LastIndexOf('/');
        if (slash <= 0)
            throw new ArgumentException("Not a key prefix of the RavenDB file store.", nameof(prefix));
        var (documentId, namePrefix) = (prefix[..slash], prefix[(slash + 1)..]);

        var found = new List<SparkStoredObject>();
        var executor = store.GetRequestExecutor();
        var command = new GetDocumentsCommand(store.Conventions, [documentId], includes: null, metadataOnly: true);
        using (executor.ContextPool.AllocateOperationContext(out JsonOperationContext context))
        {
            await executor.ExecuteAsync(command, context, sessionInfo: null, cancellationToken);
            if (command.Result?.Results is { Length: > 0 } results
                && results[0] is BlittableJsonReaderObject document
                && document.TryGet(Raven.Client.Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata)
                && metadata.TryGet(Raven.Client.Constants.Documents.Metadata.Attachments, out BlittableJsonReaderArray attachments))
            {
                metadata.TryGet(Raven.Client.Constants.Documents.Metadata.LastModified, out DateTime lastModified);
                foreach (var item in attachments)
                {
                    if (item is BlittableJsonReaderObject attachment && attachment.TryGet("Name", out string name)
                        && name.StartsWith(namePrefix, StringComparison.Ordinal))
                        found.Add(new SparkStoredObject(documentId + "/" + name, new DateTimeOffset(DateTime.SpecifyKind(lastModified, DateTimeKind.Utc))));
                }
            }
        }
        foreach (var item in found)
            yield return item;
    }

    public string StagingKey(string token) => SparkFileUpload.IdFor(token).Value + "/" + OriginalName;

    public string StagingVariantKey(string token, string variant)
        => SparkFileKeys.IsVariantName(variant) ? StagingKey(token) + "." + variant : throw new ArgumentException("Not a variant name.", nameof(variant));

    public string EntityKey(string entityId, string fileId) => entityId + "/" + fileId;

    public string EntityPrefix(string entityId) => entityId + "/";

    /// <summary>
    /// Deletes the attachment and its variants from the document, then every revision of the document that still holds one of
    /// them (SP8: a revision keeps a removed attachment readable, and its bytes live as long as a revision references them).
    /// Revisions that never held the file stay.
    /// </summary>
    public async Task EraseAsync(string entityId, string fileId, CancellationToken cancellationToken)
    {
        bool Matches(string? name) => name is not null && (name == fileId || name.StartsWith(fileId + ".", StringComparison.Ordinal));

        foreach (var item in await ListOrEmptyAsync(EntityPrefix(entityId), cancellationToken))
        {
            var (_, name) = Split(item.Key);
            if (Matches(name))
                await DeleteAsync(item.Key, cancellationToken);
        }

        var holding = new List<string>();
        using (var session = store.OpenAsyncSession())
        {
            const int page = 100;
            for (var start = 0; ; start += page)
            {
                var revisions = await session.Advanced.Revisions.GetMetadataForAsync(entityId, start, page, cancellationToken);
                foreach (var metadata in revisions)
                {
                    if (metadata.TryGetValue(Raven.Client.Constants.Documents.Metadata.Attachments, out object? attachments)
                        && attachments is IEnumerable<object> list
                        && list.OfType<Raven.Client.Documents.Session.IMetadataDictionary>().Any(a => Matches(a.TryGetValue("Name", out object? n) ? n?.ToString() : null))
                        && metadata.TryGetValue(Raven.Client.Constants.Documents.Metadata.ChangeVector, out object? changeVector)
                        && changeVector?.ToString() is { } vector)
                        holding.Add(vector);
                }
                if (revisions.Count < page)
                    break;
            }
        }
        if (holding.Count > 0)
            await store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Revisions.DeleteRevisionsOperation(entityId, holding, removeForceCreatedRevisions: true), cancellationToken);
    }

    private async Task<List<SparkStoredObject>> ListOrEmptyAsync(string prefix, CancellationToken cancellationToken)
    {
        var found = new List<SparkStoredObject>();
        await foreach (var item in ListAsync(prefix, cancellationToken))
            found.Add(item);
        return found;
    }

    /// <summary>
    /// A copy deferred into the save's session: RavenDB runs it in the same batch as the entity's put, so the attachment exists
    /// exactly when the entity commits. The staged original stays on its upload document and expires with it.
    /// </summary>
    public Task AttachAsync(SparkFileTransfer transfer, CancellationToken cancellationToken)
    {
        var (stagingDocument, stagingName) = Split(transfer.StagingKey ?? throw new ArgumentException("An attach needs the staged key.", nameof(transfer)));
        var (entityId, fileId) = Split(EntityKey(transfer.EntityId, transfer.FileId));
        Native(transfer).Advanced.Attachments.Copy(stagingDocument, stagingName, entityId, fileId);
        return Task.CompletedTask;
    }

    /// <summary>A delete deferred into the save's session, so a refused save keeps the file.</summary>
    public Task DetachAsync(SparkFileTransfer transfer, CancellationToken cancellationToken)
    {
        var (entityId, fileId) = Split(EntityKey(transfer.EntityId, transfer.FileId));
        Native(transfer).Advanced.Attachments.Delete(entityId, fileId);
        return Task.CompletedTask;
    }

    private static Raven.Client.Documents.Session.IAsyncDocumentSession Native(SparkFileTransfer transfer)
        => transfer.Session.Native as Raven.Client.Documents.Session.IAsyncDocumentSession
            ?? throw new InvalidOperationException("The RavenDB file store needs a RavenDB session.");

    /// <summary>The document id and attachment name of <paramref name="key"/>; a key this store did not make is refused.</summary>
    internal static (string DocumentId, string Name) Split(string key)
    {
        var slash = key.LastIndexOf('/');
        if (slash <= 0 || slash == key.Length - 1 || !AttachmentName().IsMatch(key[(slash + 1)..]))
            throw new ArgumentException("Not a key of the RavenDB file store.", nameof(key));
        return (key[..slash], key[(slash + 1)..]);
    }

    /// <summary>The attachment stream, disposing the result that owns its connection.</summary>
    private sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
