using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Files.Staging;
using MintPlayer.Spark.Files.Storage;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Files.Services;

/// <summary>
/// The save path's half of a file attribute (#700 D3, D9). The posted value is never copied into the entity; each value (each
/// list item) is read as one of:
/// <list type="bullet">
/// <item><see langword="null"/>: clear (a list: empty).</item>
/// <item><c>{ "uploadToken": … }</c>: claim that upload. It must be this user's, for this type, attribute and object, committed
/// and Clean, and unexpired. It is claimed once: the staging document is written with the version this save read, so a second
/// save claiming the same token concurrently fails.</item>
/// <item><c>{ "fileId": … }</c> naming a file the entity already holds: keep it (for a list, at this position). The rest of the
/// echoed value (url, metadata) is ignored.</item>
/// </list>
/// Anything else is refused, including a <c>fileId</c> the entity does not hold: a client cannot point an attribute at
/// someone else's bytes. A file the saved value no longer holds is removed from the store in the same commit.
/// </summary>
[Register(typeof(ISparkFileValueHandler), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
internal sealed partial class SparkFileValueHandler : ISparkFileValueHandler
{
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IOptions<SparkFilesOptions> options;
    [Inject] private readonly IFileStore fileStore;
    [Inject] private readonly FileClaimLedger ledger;
    [Inject] private readonly TimeProvider clock;

    public Task BeginAsync(SparkFileBeginContext context, CancellationToken cancellationToken)
    {
        // Only a type whose AsDetail rows hold files gets a snapshot; every other save leaves the ledger alone.
        Dictionary<string, List<SparkFile>>? snapshot = null;
        foreach (var (path, file) in NestedFiles(context.Root))
        {
            snapshot ??= new(StringComparer.Ordinal);
            if (!snapshot.TryGetValue(path, out var files))
                snapshot[path] = files = [];
            files.Add(file);
        }
        if (snapshot is null && !HasNestedFileAttributes(context.Root.GetType()))
            return Task.CompletedTask;

        var pending = ledger.For(context.Root);
        pending.Snapshot = snapshot ?? new(StringComparer.Ordinal);
        pending.NestedKept.Clear();
        return Task.CompletedTask;
    }

    public async Task WriteAsync(SparkFileWriteContext context, CancellationToken cancellationToken)
    {
        var name = context.Attribute.Name;
        var element = FileUploadService.StoredFileType(context.Property)
            ?? throw new InvalidOperationException($"'{name}' is not a SparkFile/SparkImage property.");
        var isList = element != (Nullable.GetUnderlyingType(context.Property.PropertyType) ?? context.Property.PropertyType);
        var definition = context.Definition;
        if (definition is null || context.EntityTypeName is null || modelLoader.GetEntityTypeByName(context.EntityTypeName) is null)
            throw new SparkValidationException($"'{name}' has no model definition; a file cannot be saved to it.", name);

        if (context.Owner is { } owner)
        {
            await WriteNestedAsync(context, owner, element, isList, definition, cancellationToken);
            return;
        }

        var entityType = modelLoader.GetEntityTypeByName(context.EntityTypeName)!;
        var held = Held(context.Property.GetValue(context.Entity));
        var target = new ClaimTarget(entityType.Id, definition, name, context.Session.GetId(context.Entity), element);

        var next = new List<SparkFile>();
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var attaches = new List<FileClaimLedger.Attach>();
        if (Posted(context.Attribute.Value) is { } value)
        {
            if (!isList)
                next.Add(await ResolveAsync(value, target, held, kept, attaches, context, cancellationToken));
            else if (value.ValueKind != JsonValueKind.Array)
                throw new SparkValidationException($"'{name}' takes a list of files.", name);
            else
            {
                foreach (var item in value.EnumerateArray())
                    next.Add(await ResolveAsync(item, target, held, kept, attaches, context, cancellationToken));
            }
        }

        var maxCount = isList ? definition.FileOptions?.MaxCount ?? options.Value.DefaultMaxCount : 1;
        if (next.Count > maxCount)
            throw new SparkValidationException($"'{name}' holds at most {maxCount} file(s).", name);

        context.Property.SetValue(context.Entity, isList ? ToList(context.Property.PropertyType, element, next) : next.FirstOrDefault());

        var pending = ledger.For(context.Entity);
        pending.Attaches.AddRange(attaches);
        pending.Detaches.AddRange(held.Where(h => !kept.Contains(h.FileId)).SelectMany(StoredIds));
    }

    /// <summary>
    /// A file inside an AsDetail row (#700 M10). A row has no id to bind an upload to, and a new row has no key until it is
    /// saved, so the claim is bound to the document and the attribute path (<c>ServiceEntries.Invoice</c>). A keep may name any
    /// file a row under that path held when the save began, each once across all rows; which files are removed is decided
    /// after the write (<see cref="OnStoredAsync"/>), since a dropped row is never written to.
    /// </summary>
    private async Task WriteNestedAsync(SparkFileWriteContext context, SparkFileOwner owner, Type element, bool isList,
        EntityAttributeDefinition definition, CancellationToken cancellationToken)
    {
        var path = owner.AttributePath;
        var pending = ledger.For(owner.Root);
        if (owner.RootTypeName is null || modelLoader.GetEntityTypeByName(owner.RootTypeName) is not { } rootType || pending.Snapshot is null)
            throw new SparkValidationException($"'{path}' has no model definition; a file cannot be saved to it.", path);

        var held = pending.Snapshot.TryGetValue(path, out var files) ? files : [];
        if (!pending.NestedKept.TryGetValue(path, out var kept))
            pending.NestedKept[path] = kept = new HashSet<string>(StringComparer.Ordinal);
        var target = new ClaimTarget(rootType.Id, definition, path, context.Session.GetId(owner.Root), element);

        var next = new List<SparkFile>();
        var attaches = new List<FileClaimLedger.Attach>();
        if (Posted(context.Attribute.Value) is { } value)
        {
            if (!isList)
                next.Add(await ResolveAsync(value, target, held, kept, attaches, context, cancellationToken));
            else if (value.ValueKind != JsonValueKind.Array)
                throw new SparkValidationException($"'{path}' takes a list of files.", path);
            else
            {
                foreach (var item in value.EnumerateArray())
                    next.Add(await ResolveAsync(item, target, held, kept, attaches, context, cancellationToken));
            }
        }

        var maxCount = isList ? definition.FileOptions?.MaxCount ?? options.Value.DefaultMaxCount : 1;
        if (next.Count > maxCount)
            throw new SparkValidationException($"'{path}' holds at most {maxCount} file(s).", path);

        context.Property.SetValue(context.Entity, isList ? ToList(context.Property.PropertyType, element, next) : next.FirstOrDefault());
        pending.Attaches.AddRange(attaches);
    }

    public async Task OnStoredAsync(SparkFileStoredContext context, CancellationToken cancellationToken)
    {
        if (ledger.TakePending(context.Entity) is not { } pending)
            return;

        if (pending.Snapshot is { } snapshot)
        {
            // What the rows hold now, walked after the write: rows the save dropped, restored or kept all count as they are.
            var heldNow = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, file) in NestedFiles(context.Entity))
            {
                if (!heldNow.Add(file.FileId))
                    throw new SparkValidationException($"'{path}' holds the same file twice; each file belongs to one row.", path);
            }
            pending.Detaches.AddRange(snapshot.Values.SelectMany(f => f).Where(f => !heldNow.Contains(f.FileId)).SelectMany(StoredIds));
        }

        if (fileStore is ISessionFileStore sessionStore)
        {
            // In the save's unit of work: the files commit, or don't, with the entity.
            foreach (var transfer in pending.Attaches.SelectMany(Transfers))
                await sessionStore.AttachAsync(new SparkFileTransfer(context.Session, context.Id, transfer.FileId, transfer.StagingKey), cancellationToken);
            foreach (var fileId in pending.Detaches)
                await sessionStore.DetachAsync(new SparkFileTransfer(context.Session, context.Id, fileId, StagingKey: null), cancellationToken);
            return;
        }

        // A store outside the session: copy now, so the entity never commits pointing at missing bytes; delete only once it
        // committed (FileStoreInterceptor), so a refused save loses nothing. A copy whose save then fails is held by no entity,
        // and the reconcile removes it.
        var transfers = pending.Attaches.SelectMany(Transfers).ToList();
        foreach (var transfer in transfers)
            await fileStore.CopyAsync(transfer.StagingKey, fileStore.EntityKey(context.Id, transfer.FileId), cancellationToken);
        ledger.DeleteAfterCommit(context.Entity,
            transfers.Select(t => t.StagingKey).Concat(pending.Detaches.Select(fileId => fileStore.EntityKey(context.Id, fileId))));
    }

    /// <summary>The objects one attach moves: the file, then each of its variants.</summary>
    private static IEnumerable<FileClaimLedger.Transfer> Transfers(FileClaimLedger.Attach attach)
        => attach.Variants.Prepend(new FileClaimLedger.Transfer(attach.FileId, attach.StagingKey));

    /// <summary>The file ids a held file occupies in the store: its own and its variants'.</summary>
    private static IEnumerable<string> StoredIds(SparkFile file)
        => file is SparkImage image ? image.Variants.Values.Select(v => v.FileId).Where(id => id.Length > 0).Prepend(file.FileId) : [file.FileId];

    /// <summary>What a claim is bound to: the type, the attribute (or AsDetail path) and the object id an upload must name.</summary>
    private sealed record ClaimTarget(Guid EntityTypeId, EntityAttributeDefinition Attribute, string AttributeName, string? EntityId, Type Element);

    /// <summary>The AsDetail attributes of <paramref name="rootType"/>'s model whose row type has a file property.</summary>
    private IEnumerable<(EntityAttributeDefinition Attribute, PropertyInfo Property, PropertyInfo[] FileProperties)> NestedFileAttributes(Type rootType)
    {
        if (modelLoader.GetEntityTypeByClrType(rootType.FullName ?? rootType.Name) is not { } definition)
            yield break;
        foreach (var attribute in definition.Attributes.Where(a => a.DataType == "AsDetail"))
        {
            if (rootType.GetProperty(attribute.Name, BindingFlags.Public | BindingFlags.Instance) is not { } property)
                continue;
            var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var rowType = SparkModelShape.GetCollectionElementType(underlying) ?? underlying;
            var fileProperties = rowType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => FileUploadService.StoredFileType(p) is not null)
                .ToArray();
            if (fileProperties.Length > 0)
                yield return (attribute, property, fileProperties);
        }
    }

    private bool HasNestedFileAttributes(Type rootType) => NestedFileAttributes(rootType).Any();

    /// <summary>Every file <paramref name="root"/>'s AsDetail rows hold, with its attribute path.</summary>
    private IEnumerable<(string Path, SparkFile File)> NestedFiles(object root)
    {
        foreach (var (attribute, property, fileProperties) in NestedFileAttributes(root.GetType()))
        {
            IEnumerable<object?> rows = property.GetValue(root) switch
            {
                null => [],
                IEnumerable many and not string => many.Cast<object?>(),
                var single => [single],
            };
            foreach (var row in rows)
            {
                if (row is null) continue;
                foreach (var fileProperty in fileProperties)
                foreach (var file in Held(fileProperty.GetValue(row)))
                    yield return ($"{attribute.Name}.{fileProperty.Name}", file);
            }
        }
    }

    private async Task<SparkFile> ResolveAsync(
        JsonElement item, ClaimTarget target, IReadOnlyList<SparkFile> held, HashSet<string> kept,
        List<FileClaimLedger.Attach> attaches, SparkFileWriteContext context, CancellationToken cancellationToken)
    {
        var name = target.AttributeName;
        if (item.ValueKind == JsonValueKind.Object)
        {
            if (Text(item, "uploadToken") is { } token)
                return await ClaimAsync(token, target, attaches, context, cancellationToken);

            // Keep: only a file this entity already holds, each once.
            if (Text(item, "fileId") is { } fileId && held.FirstOrDefault(h => h.FileId == fileId) is { } existing && kept.Add(fileId))
                return existing;
        }
        throw new SparkValidationException($"'{name}' can only keep a file it holds or claim a new upload.", name);
    }

    private async Task<SparkFile> ClaimAsync(
        string token, ClaimTarget target, List<FileClaimLedger.Attach> attaches, SparkFileWriteContext context, CancellationToken cancellationToken)
    {
        var name = target.AttributeName;
        var userId = currentUser.Id;
        var now = clock.GetUtcNow();
        // Every refusal reads the same: which check failed is the server's business, not the caller's.
        var refusal = new SparkValidationException($"The upload for '{name}' can't be used. Upload the file again.", name);

        if (string.IsNullOrEmpty(userId) || token.Length is 0 or > 64 || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw refusal;
        var upload = await context.Session.LoadAsync<SparkFileUpload>(SparkFileUpload.IdFor(token), cancellationToken);
        if (upload is null
            || upload.State != SparkFileUploadState.Committed
            || upload.ScanStatus != SparkFileScanStatus.Clean
            || upload.UserId != userId
            || upload.EntityTypeId != target.EntityTypeId
            || upload.Attribute != name
            || upload.DataType != target.Attribute.DataType
            || !string.Equals(upload.ObjectId, target.EntityId, StringComparison.OrdinalIgnoreCase)
            || (upload.ExpiresAtUtc is { } expires && expires <= now.UtcDateTime))
            throw refusal;

        // Single use, also against a concurrent save: the claim is written with the version this save read.
        upload.State = SparkFileUploadState.Claimed;
        await context.Session.StoreAsync(upload, context.Session.GetVersion(upload), upload.Id!, cancellationToken);

        var file = (SparkFile)Activator.CreateInstance(target.Element)!;
        file.FileId = Guid.NewGuid().ToString("N");
        var variants = new List<FileClaimLedger.Transfer>();
        if (file is SparkImage image)
        {
            image.Width = upload.Width ?? 0;
            image.Height = upload.Height ?? 0;
            foreach (var (variantName, staged) in upload.Variants ?? [])
            {
                var variantId = SparkFileKeys.VariantFileId(file.FileId, variantName);
                image.Variants[variantName] = new SparkImageVariant
                {
                    FileId = variantId,
                    ContentType = staged.ContentType,
                    Width = staged.Width,
                    Height = staged.Height,
                    Length = staged.Length,
                };
                variants.Add(new FileClaimLedger.Transfer(variantId, fileStore.StagingVariantKey(upload.Token, variantName)));
            }
        }
        file.FileName = upload.FileName;
        file.ContentType = upload.ContentType ?? "application/octet-stream";
        file.Length = upload.Length;
        file.Sha256 = upload.Sha256;
        file.ScanStatus = upload.ScanStatus;
        file.UploadedAt = now;
        file.UploadedBy = userId;

        attaches.Add(new FileClaimLedger.Attach(file.FileId, upload.StagingKey, new FileClaimLedger.AcceptedFile(target.Attribute, file, userId), variants));
        return file;
    }

    /// <summary>The files the entity holds now: what a keep may name, and what is removed when it is not kept.</summary>
    private static IReadOnlyList<SparkFile> Held(object? value) => value switch
    {
        null => [],
        SparkFile single => [single],
        IEnumerable many => many.OfType<SparkFile>().ToList(),
        _ => [],
    };

    /// <summary>The posted value as JSON, or null for a clear. A value set from code (not JSON) is read the same way.</summary>
    private static JsonElement? Posted(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement element => element,
        _ => JsonSerializer.SerializeToElement(value, value.GetType(), JsonSerializerOptions.Web),
    };

    private static string? Text(JsonElement item, string property)
    {
        foreach (var member in item.EnumerateObject())
        {
            if (string.Equals(member.Name, property, StringComparison.OrdinalIgnoreCase))
                return member.Value.ValueKind == JsonValueKind.String ? member.Value.GetString() : null;
        }
        return null;
    }

    private static object ToList(Type propertyType, Type element, List<SparkFile> files)
    {
        if (propertyType.IsArray)
        {
            var array = Array.CreateInstance(element, files.Count);
            for (var i = 0; i < files.Count; i++)
                array.SetValue(files[i], i);
            return array;
        }
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
        foreach (var file in files)
            list.Add(file);
        return list;
    }
}
