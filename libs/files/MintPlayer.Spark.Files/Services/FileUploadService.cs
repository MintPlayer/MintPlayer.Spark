using System.Reflection;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Files.Images;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Files.Scanning;
using MintPlayer.Spark.Files.Sniffing;
using MintPlayer.Spark.Files.Staging;
using MintPlayer.Spark.Files.Storage;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Files.Services;

/// <summary>
/// The three upload steps (#700 D3): <b>prepare</b> issues a slot per file to a user allowed to set the attribute,
/// <b>upload</b> takes the bytes (through Spark, for a store without direct uploads), and <b>commit</b> reads them back,
/// sizes, sniffs, validates and scans them. Only then can a save claim the token.
/// </summary>
[Register(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
internal sealed partial class FileUploadService
{
    /// <summary>Where the browser posts bytes for a store without direct uploads.</summary>
    public const string UploadPath = "/spark/files/upload";

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly IAttributeRights attributeRights;
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IFileStore fileStore;
    [Inject] private readonly FileSniffer sniffer;
    [Inject] private readonly UploadRateLimiter rateLimiter;
    [Inject] private readonly IOptions<SparkFilesOptions> options;
    [Inject] private readonly IEnumerable<IFileUploadHandler> uploadHandlers;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly FileScanRunner scanRunner;
    [Inject] private readonly DeferredFileScanner deferredScanner;
    [Inject] private readonly IEnumerable<IImageProcessor> imageProcessors;
    [Inject] private readonly ImageProcessingGate imageGate;
    [Inject] private readonly ILogger<FileUploadService> logger;

    /// <summary>Staged bytes to delete once the refusals are saved (see <see cref="CommitAsync"/>).</summary>
    private readonly List<string> refusedKeys = [];

    /// <summary>Tokens for the deferred scan, queued once their <c>Pending</c> state is saved.</summary>
    private readonly List<string> deferredTokens = [];

    public async Task<FilesResult<PrepareFilesOutput>> PrepareAsync(PrepareFilesInput? input, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { Length: > 0 } userId)
            return FilesResult<PrepareFilesOutput>.Fail(FilesOutcome.Unauthorized);
        if (input?.Files is not { Count: > 0 } files || string.IsNullOrEmpty(input.Attribute))
            return FilesResult<PrepareFilesOutput>.Fail(FilesOutcome.Invalid, "Name the attribute and at least one file.");

        var settings = options.Value;
        if (files.Count > settings.MaxBatch)
            return FilesResult<PrepareFilesOutput>.Fail(FilesOutcome.Invalid, $"At most {settings.MaxBatch} files at once.");

        var target = await ResolveTargetAsync(input.ObjectTypeId, input.Attribute, input.Id, cancellationToken);
        if (target.Failure is { } failure)
            return FilesResult<PrepareFilesOutput>.Fail(failure.Outcome, failure.Error);
        var (entityType, attribute, isList) = (target.EntityType!, target.Attribute!, target.IsList);

        var maxCount = isList ? attribute.FileOptions?.MaxCount ?? settings.DefaultMaxCount : 1;
        if (files.Count > maxCount)
            return FilesResult<PrepareFilesOutput>.Fail(FilesOutcome.Invalid, $"'{attribute.Name}' holds at most {maxCount} file(s).");

        var maxBytes = Math.Min(attribute.FileOptions?.MaxBytes ?? settings.MaxFileBytes, settings.MaxFileBytes);
        foreach (var file in files)
        {
            if (file.Length <= 0)
                return FilesResult<PrepareFilesOutput>.Fail(FilesOutcome.Invalid, "A file is empty.");
            if (file.Length > maxBytes)
                return FilesResult<PrepareFilesOutput>.Fail(FilesOutcome.TooLarge, $"A file is larger than {maxBytes} bytes.");
        }

        if (!rateLimiter.TryAcquire(userId, files.Count))
            return FilesResult<PrepareFilesOutput>.Fail(FilesOutcome.TooManyRequests, "Too many uploads; try again in a minute.");

        var now = clock.GetUtcNow();
        var uploadDeadline = now + settings.UploadWindow;
        var prepared = new List<PreparedFile>(files.Count);
        using var session = store.OpenAsyncSession();
        foreach (var file in files)
        {
            var token = NewToken();
            var stagingKey = fileStore.StagingKey(token);
            var upload = new SparkFileUpload
            {
                Id = SparkFileUpload.IdFor(token),
                Token = token,
                UserId = userId,
                EntityTypeId = entityType.Id,
                Attribute = target.Path!,
                ObjectId = input.Id,
                DataType = attribute.DataType,
                State = SparkFileUploadState.Prepared,
                FileName = FileNameSanitizer.Sanitize(file.FileName),
                DeclaredLength = file.Length,
                MaxBytes = maxBytes,
                StagingKey = stagingKey,
                UploadDeadlineUtc = uploadDeadline.UtcDateTime,
                CreatedAtUtc = now.UtcDateTime,
                ExpiresAtUtc = (now + settings.StagingLifetime).UtcDateTime,
            };
            await session.StoreAsync(upload, upload.Id, cancellationToken);

            var direct = await fileStore.CreateUploadTargetAsync(stagingKey, maxBytes, uploadDeadline, cancellationToken);
            prepared.Add(direct is null
                ? new PreparedFile(token, upload.FileName, UploadPath, SparkFileUploadTarget.Post, new Dictionary<string, string> { ["token"] = token }, NoHeaders, Direct: false)
                : new PreparedFile(token, upload.FileName, direct.Url, direct.Method, direct.Fields, direct.Headers, Direct: true));
        }
        await session.SaveChangesAsync(cancellationToken);
        return FilesResult<PrepareFilesOutput>.Ok(new PrepareFilesOutput(prepared));
    }

    /// <summary>The bytes of <paramref name="token"/>, streamed into the store and cut off past the slot's cap.</summary>
    public async Task<FilesResult<bool>> UploadAsync(string? token, Stream content, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { Length: > 0 } userId)
            return FilesResult<bool>.Fail(FilesOutcome.Unauthorized);
        if (string.IsNullOrEmpty(token) || token.Length > 64)
            return FilesResult<bool>.Fail(FilesOutcome.NotFound);

        using var session = store.OpenAsyncSession();
        var upload = await session.LoadAsync<SparkFileUpload>(SparkFileUpload.IdFor(token), cancellationToken);
        // Another user's token, a used one and an expired one answer alike: there is no such slot.
        if (upload is null || upload.UserId != userId || upload.State != SparkFileUploadState.Prepared
            || upload.UploadDeadlineUtc < clock.GetUtcNow().UtcDateTime || IsExpired(upload))
            return FilesResult<bool>.Fail(FilesOutcome.NotFound);

        var limited = new LengthLimitedStream(content, upload.MaxBytes);
        try
        {
            await fileStore.WriteAsync(upload.StagingKey, limited, contentType: null, cancellationToken);
        }
        catch (Exception) when (limited.Exceeded)
        {
            await fileStore.DeleteAsync(upload.StagingKey, CancellationToken.None);
            return FilesResult<bool>.Fail(FilesOutcome.TooLarge, $"The file is larger than {upload.MaxBytes} bytes.");
        }

        upload.State = SparkFileUploadState.Uploaded;
        await session.SaveChangesAsync(cancellationToken);
        return FilesResult<bool>.Ok(true);
    }

    public async Task<FilesResult<CommitFilesOutput>> CommitAsync(CommitFilesInput? input, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { Length: > 0 } userId)
            return FilesResult<CommitFilesOutput>.Fail(FilesOutcome.Unauthorized);
        if (input?.Tokens is not { Count: > 0 } tokens || tokens.Count > options.Value.MaxBatch
            || tokens.Any(t => string.IsNullOrEmpty(t) || t.Length > 64))
            return FilesResult<CommitFilesOutput>.Fail(FilesOutcome.Invalid, $"Commit 1 to {options.Value.MaxBatch} tokens.");

        using var session = store.OpenAsyncSession();
        var loaded = await session.LoadAsync<SparkFileUpload>(tokens.Distinct().Select(SparkFileUpload.IdFor), cancellationToken);
        var results = new List<CommittedFile>(tokens.Count);
        foreach (var token in tokens)
        {
            var upload = loaded.GetValueOrDefault(SparkFileUpload.IdFor(token));
            if (upload is null || upload.UserId != userId || IsExpired(upload))
            {
                results.Add(new CommittedFile(token, null, null, 0, SparkFileScanStatus.Pending, null, null, "No such upload."));
                continue;
            }
            if (upload.State is SparkFileUploadState.Uploaded || (upload.State is SparkFileUploadState.Prepared && upload.UploadDeadlineUtc >= clock.GetUtcNow().UtcDateTime))
                await CommitOneAsync(upload, userId, cancellationToken);
            else if (upload.State is SparkFileUploadState.Prepared)
                Refuse(upload, "No file was uploaded.");
            else if (options.Value.ScanMode == SparkFileScanMode.Deferred && DeferredFileScanner.WaitsForScan(upload))
                deferredTokens.Add(upload.Token);
            // Committed, Refused and Claimed answer what they already are: a repeated commit is harmless.
            results.Add(ToCommitted(upload));
        }
        await session.SaveChangesAsync(cancellationToken);
        // After the save, never before: a document saved after its attachment was deleted keeps the attachment listed.
        // Should a delete fail, the document is already Refused and its expiry takes the bytes with it.
        foreach (var key in refusedKeys)
            await fileStore.DeleteAsync(key, CancellationToken.None);
        refusedKeys.Clear();
        foreach (var token in deferredTokens)
            deferredScanner.Enqueue(token);
        deferredTokens.Clear();
        return FilesResult<CommitFilesOutput>.Ok(new CommitFilesOutput(results));
    }

    private async Task CommitOneAsync(SparkFileUpload upload, string userId, CancellationToken cancellationToken)
    {
        var entityType = modelLoader.GetEntityType(upload.EntityTypeId);
        var attribute = entityType is null ? null : SparkFileAttributePath.Resolve(modelLoader, entityType, upload.Attribute)?.Attribute;
        if (entityType is null || attribute is null)
        {
            RefuseAndDelete(upload, "The attribute no longer exists.");
            return;
        }

        // Read back, whoever wrote it: a direct upload's bytes are as untrusted as any.
        var header = new byte[FileSniffer.HeaderLength];
        int headerLength = 0;
        long length = 0;
        string sha256;
        await using (var stream = await fileStore.OpenReadAsync(upload.StagingKey, cancellationToken))
        {
            if (stream is null)
            {
                Refuse(upload, "No file was uploaded.");
                return;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (headerLength < header.Length)
                {
                    var take = Math.Min(read, header.Length - headerLength);
                    Array.Copy(buffer, 0, header, headerLength, take);
                    headerLength += take;
                }
                length += read;
                // Defence in depth: a store that cannot cap a direct upload (Azure SAS) is capped here.
                if (length > upload.MaxBytes)
                    break;
                hash.AppendData(buffer, 0, read);
            }
            sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        if (length > upload.MaxBytes)
        {
            RefuseAndDelete(upload, $"The file is larger than {upload.MaxBytes} bytes.");
            return;
        }

        string contentType;
        if (upload.ImageProcessed && upload.ContentType is { } reencoded)
        {
            // A retried commit of an image already re-encoded: the staged bytes are the processor's output (a GIF is a PNG by
            // now, which the accept list may not name), so the type recorded then stands. The read-back above re-hashed them.
            contentType = reencoded;
        }
        else
        {
            var sniffed = sniffer.Sniff(header.AsSpan(0, headerLength), upload.FileName, FileSniffer.EffectiveAccept(attribute.DataType, attribute.FileOptions));
            if (!sniffed.IsAccepted)
            {
                RefuseAndDelete(upload, sniffed.Error!);
                return;
            }
            contentType = sniffed.ContentType!;
        }

        // An Image is re-encoded before anything else sees it (SP5: the re-encode is the sanitiser), so the handlers and the
        // scanner judge the bytes that are stored. A retried commit (a failed scan) does not re-encode a second time.
        if (attribute.DataType == SparkFileDataTypes.Image && imageProcessors.LastOrDefault() is { } processor && !upload.ImageProcessed)
        {
            if (await ProcessImageAsync(processor, upload, attribute, contentType, cancellationToken) is not { } processed)
                return;
            (contentType, length, sha256) = processed;
        }

        var context = new FileUploadContext(entityType, attribute, upload.ObjectId, userId, upload.FileName, contentType, length, sha256,
            async ct => await fileStore.OpenReadAsync(upload.StagingKey, ct) ?? throw new InvalidOperationException("The staged file is gone."));
        foreach (var handler in uploadHandlers)
        {
            var decision = await handler.ValidateAsync(context, cancellationToken);
            if (decision.IsRefused)
            {
                RefuseAndDelete(upload, decision.RefusalMessage!);
                return;
            }
        }

        upload.ContentType = contentType;
        upload.Length = length;
        upload.Sha256 = sha256;
        upload.Error = null;

        // Last, after every cheaper refusal. Without a scanner the startup guard (D5) already decided unscanned is allowed.
        if (scanRunner.Scanner is not { } scanner)
        {
            upload.ScanStatus = SparkFileScanStatus.Clean;
            upload.State = SparkFileUploadState.Committed;
            return;
        }
        if (options.Value.ScanMode == SparkFileScanMode.Deferred)
        {
            upload.ScanStatus = SparkFileScanStatus.Pending;
            upload.State = SparkFileUploadState.Committed;
            deferredTokens.Add(upload.Token);
            return;
        }
        var scanContext = new FileScanContext(entityType, attribute, userId, upload.FileName, contentType, length, sha256);
        var result = await scanRunner.ScanAsync(scanner, upload, scanContext, cancellationToken);
        if (scanRunner.Apply(upload, result))
            refusedKeys.AddRange(fileStore.StagedKeys(upload));
    }

    /// <summary>
    /// Re-encodes the staged image in place and stages its variants next to it. Returns the stored bytes' type, length and
    /// hash, or null when the image was refused (over the pixel cap, undecodable, or the processor failed: fail-closed).
    /// </summary>
    private async Task<(string ContentType, long Length, string Sha256)?> ProcessImageAsync(
        IImageProcessor processor, SparkFileUpload upload, EntityAttributeDefinition attribute, string contentType, CancellationToken cancellationToken)
    {
        var maxPixels = Math.Min(options.Value.MaxImagePixels, attribute.FileOptions?.MaxPixels ?? long.MaxValue);
        var variants = attribute.FileOptions?.Variants ?? options.Value.DefaultImageVariants;
        ProcessedImage processed;
        await using (var staged = await fileStore.OpenReadAsync(upload.StagingKey, cancellationToken))
        {
            if (staged is null)
            {
                Refuse(upload, "No file was uploaded.");
                return null;
            }
            await using var source = await SeekableSpool.SpoolAsync(staged, cancellationToken);
            try
            {
                using var turn = await imageGate.EnterAsync(cancellationToken);
                processed = await processor.ProcessAsync(source, new ImageProcessingRequest(contentType, maxPixels, variants), cancellationToken);
            }
            catch (ImageRejectedException rejected)
            {
                RefuseAndDelete(upload, rejected.Message);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "The image processor failed on upload {Token}; the upload is refused.", upload.Token);
                RefuseAndDelete(upload, ImageRejectedException.Undecodable().Message);
                return null;
            }
        }

        await using (processed)
        {
            if (processed.Variants.Any(v => !variants.ContainsKey(v.Name)) || processed.Variants.Select(v => v.Name).Distinct(StringComparer.Ordinal).Count() != processed.Variants.Count)
            {
                logger.LogError("{Processor} returned variants that were not asked for; upload {Token} is refused.", processor.GetType().Name, upload.Token);
                RefuseAndDelete(upload, ImageRejectedException.Undecodable().Message);
                return null;
            }

            // The hash is of what is stored, so it is taken while writing.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var hashing = new HashingReadStream(processed.Content, hash);
            await fileStore.WriteAsync(upload.StagingKey, hashing, processed.ContentType, cancellationToken);

            upload.Variants = [];
            foreach (var variant in processed.Variants)
            {
                var counting = new HashingReadStream(variant.Content, hash: null);
                await fileStore.WriteAsync(fileStore.StagingVariantKey(upload.Token, variant.Name), counting, variant.ContentType, cancellationToken);
                upload.Variants[variant.Name] = new SparkImageVariant
                {
                    ContentType = variant.ContentType,
                    Width = variant.Width,
                    Height = variant.Height,
                    Length = counting.BytesRead,
                };
            }

            upload.Width = processed.Width;
            upload.Height = processed.Height;
            upload.ImageProcessed = true;
            upload.FileName = FileNameSanitizer.WithExtensionFor(upload.FileName, processed.ContentType);
            return (processed.ContentType, hashing.BytesRead, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
    }

    private void RefuseAndDelete(SparkFileUpload upload, string error)
    {
        Refuse(upload, error);
        refusedKeys.AddRange(fileStore.StagedKeys(upload));
    }

    private static void Refuse(SparkFileUpload upload, string error)
    {
        upload.State = SparkFileUploadState.Refused;
        upload.Error = error;
    }

    private static CommittedFile ToCommitted(SparkFileUpload upload) => new(
        upload.Token,
        upload.FileName,
        upload.ContentType,
        upload.Length,
        upload.ScanStatus,
        upload.Width,
        upload.Height,
        upload.State switch
        {
            SparkFileUploadState.Refused => upload.Error,
            SparkFileUploadState.Claimed => "The upload was already used.",
            // Not refused, not claimable yet: a scan that did not reach a verdict says so (D9 fail-closed).
            _ when upload.ScanStatus == SparkFileScanStatus.ScanFailed => upload.Error,
            _ => null,
        });

    private bool IsExpired(SparkFileUpload upload) => upload.ExpiresAtUtc is { } expires && expires <= clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// The attribute a slot is for, if the caller may set it: <c>New</c> on the type (create) or a save of the existing
    /// row (edit, row security included), plus the attribute right for that verb, on an editable stored-file attribute.
    /// A row the caller can't see is not found.
    /// </summary>
    private async Task<UploadTarget> ResolveTargetAsync(Guid objectTypeId, string attributeName, string? id, CancellationToken cancellationToken)
    {
        var entityType = modelLoader.GetEntityType(objectTypeId);
        // A top-level file attribute, or (M10) an AsDetail row's file attribute as Parent.Field: the upload is bound to the
        // document and the path, since a row has no id of its own (a new row not even a key).
        if (entityType is null || SparkFileAttributePath.Resolve(modelLoader, entityType, attributeName) is not { } resolved)
            return UploadTarget.Failed(FilesOutcome.NotFound);
        var (attribute, property) = (resolved.Attribute, resolved.Property);

        var verb = id is null ? "New" : "Edit";
        try
        {
            if (id is null)
            {
                await permissions.EnsureAuthorizedAsync("New", entityType.Name, cancellationToken);
            }
            else
            {
                var existing = await databaseAccess.GetPersistentObjectAsync(entityType.Id, id);
                if (existing is null)
                    return UploadTarget.Failed(FilesOutcome.NotFound);
                await databaseAccess.EnsureSaveAuthorizedAsync(existing);
            }
        }
        catch (SparkAccessDeniedException)
        {
            return UploadTarget.Failed(FilesOutcome.Forbidden);
        }

        if (resolved.Parent is { } parent)
        {
            // The AsDetail attribute itself must be one the caller may write, and the row's file attribute one the caller may set
            // on the row type: Edit for a stored document's rows, New for a new document's (the same rights the save applies).
            if (parent.IsReadOnly || !(await attributeRights.GetEffectiveAsync(entityType, verb, cancellationToken)).IsAllowed(parent.Name))
                return UploadTarget.Failed(FilesOutcome.Forbidden);
            if (attribute.IsReadOnly || !(await attributeRights.GetEffectiveAsync(resolved.RowType!, verb, cancellationToken)).IsAllowed(attribute.Name))
                return UploadTarget.Failed(FilesOutcome.Forbidden);
        }
        else if (attribute.IsReadOnly || !(await attributeRights.GetEffectiveAsync(entityType, verb, cancellationToken)).IsAllowed(attribute.Name))
            return UploadTarget.Failed(FilesOutcome.Forbidden);

        var path = resolved.Parent is { } p ? $"{p.Name}.{attribute.Name}" : attribute.Name;
        return new UploadTarget(entityType, attribute, path, SparkModelShape.GetCollectionElementType(property.PropertyType) is not null, null);
    }

    /// <summary>The stored-file type of a property (<c>SparkFile</c>, <c>SparkImage</c> or a collection of them), else null.</summary>
    internal static Type? StoredFileType(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var element = SparkModelShape.GetCollectionElementType(type) ?? type;
        return typeof(SparkFile).IsAssignableFrom(element) ? element : null;
    }

    private static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record UploadTarget(EntityTypeDefinition? EntityType, EntityAttributeDefinition? Attribute, string? Path, bool IsList, (FilesOutcome Outcome, string? Error)? Failure)
    {
        public static UploadTarget Failed(FilesOutcome outcome, string? error = null) => new(null, null, null, false, (outcome, error));
    }
}

/// <summary>Prepared files per user per minute (#700 §4.4).</summary>
internal sealed class UploadRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string>? limiter;

    private readonly int permits;

    public UploadRateLimiter(IOptions<SparkFilesOptions> options)
    {
        permits = options.Value.UploadsPerMinute;
        if (permits > 0)
            limiter = PartitionedRateLimiter.Create<string, string>(user => RateLimitPartition.GetFixedWindowLimiter(user, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    }

    public bool TryAcquire(string userId, int count)
    {
        if (limiter is null)
            return true;
        if (count > permits)
            return false;
        using var lease = limiter.AttemptAcquire(userId, count);
        return lease.IsAcquired;
    }

    public void Dispose() => limiter?.Dispose();
}
