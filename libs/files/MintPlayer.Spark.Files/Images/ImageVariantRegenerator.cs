using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Files.Services;
using MintPlayer.Spark.Files.Storage;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Files.Images;

/// <summary>What one <c>--spark-regenerate-variants</c> run did.</summary>
/// <param name="Images">Images looked at.</param>
/// <param name="Regenerated">Images whose variants were made again.</param>
/// <param name="Failed">Images that could not be processed (their original is missing or does not decode); left as they were.</param>
public sealed record ImageVariantRegeneration(int Images, int Regenerated, int Failed);

/// <summary>
/// Makes the variants of stored images again (#700 M8): after an attribute's <c>fileOptions.variants</c> changed, after the
/// default variants changed, or for images stored before a processor was registered. Each image's variants are made from
/// its stored original; the original itself is not rewritten. An image whose variants already match the configuration is
/// skipped unless <c>force</c> is set. Variants no longer configured are deleted once the entity is saved without them.
/// </summary>
/// <remarks>
/// Only top-level <c>Image</c> properties of entity types are visited (not images inside AsDetail rows). Each entity is
/// saved with optimistic concurrency; one edited in the meantime is retried once and otherwise reported as failed.
/// </remarks>
[Register(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
internal sealed partial class ImageVariantRegenerator
{
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IFileStore fileStore;
    [Inject] private readonly IEnumerable<IImageProcessor> processors;
    [Inject] private readonly IOptions<SparkFilesOptions> options;
    [Inject] private readonly ILogger<ImageVariantRegenerator> logger;

    private static readonly MethodInfo RunTypeMethod = typeof(ImageVariantRegenerator).GetMethod(nameof(RunTypeAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public async Task<ImageVariantRegeneration> RunAsync(bool force, CancellationToken cancellationToken)
    {
        var processor = processors.LastOrDefault()
            ?? throw new InvalidOperationException("No IImageProcessor is registered: call files.UseSkiaSharpImages() (or another processor) first.");
        var total = new ImageVariantRegeneration(0, 0, 0);
        foreach (var entityType in modelLoader.GetEntityTypes())
        {
            var clrType = SparkTypeResolver.ResolveClrType(entityType.ClrType);
            if (clrType is null)
                continue;
            var attributes = entityType.Attributes
                .Where(a => a.DataType == SparkFileDataTypes.Image)
                .Select(a => (Attribute: a, Property: clrType.GetCachedProperty(a.Name)))
                .Where(p => p.Property is not null && FileUploadService.StoredFileType(p.Property) is { } element && typeof(SparkImage).IsAssignableFrom(element))
                .Select(p => (p.Attribute, Property: p.Property!))
                .ToList();
            if (attributes.Count == 0)
                continue;

            var task = (Task<ImageVariantRegeneration>)RunTypeMethod.MakeGenericMethod(clrType).Invoke(this, [processor, attributes, force, cancellationToken])!;
            var part = await task;
            total = new ImageVariantRegeneration(total.Images + part.Images, total.Regenerated + part.Regenerated, total.Failed + part.Failed);
        }
        return total;
    }

    private async Task<ImageVariantRegeneration> RunTypeAsync<T>(
        IImageProcessor processor, List<(EntityAttributeDefinition Attribute, PropertyInfo Property)> attributes, bool force, CancellationToken cancellationToken)
        where T : class
    {
        var ids = new List<string>();
        using (var session = store.OpenAsyncSession())
        {
            await using var stream = await session.Advanced.StreamAsync(session.Advanced.AsyncDocumentQuery<T>(), cancellationToken);
            while (await stream.MoveNextAsync())
                ids.Add(stream.Current.Id);
        }

        int images = 0, regenerated = 0, failed = 0;
        foreach (var id in ids)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var (seen, made, broken) = await RunEntityAsync<T>(processor, attributes, id, force, cancellationToken);
                    (images, regenerated, failed) = (images + seen, regenerated + made, failed + broken);
                    break;
                }
                catch (ConcurrencyException) when (attempt == 0)
                {
                    // Edited while its variants were made: once more, from the new version.
                }
                catch (ConcurrencyException)
                {
                    logger.LogWarning("{Id} kept changing while its image variants were regenerated; skipped.", id);
                    failed++;
                    break;
                }
            }
        }
        return new ImageVariantRegeneration(images, regenerated, failed);
    }

    private async Task<(int Images, int Regenerated, int Failed)> RunEntityAsync<T>(
        IImageProcessor processor, List<(EntityAttributeDefinition Attribute, PropertyInfo Property)> attributes, string id, bool force, CancellationToken cancellationToken)
        where T : class
    {
        // Phase 1: make and write the variants. Writing an object can move the entity's own version (a RavenDB attachment
        // does), so nothing is saved from this read.
        T? entity;
        using (var read = store.OpenAsyncSession())
            entity = await read.LoadAsync<T>(id, cancellationToken);
        if (entity is null)
            return (0, 0, 0);

        int images = 0, failed = 0;
        var made = new Dictionary<string, Made>(StringComparer.Ordinal);
        foreach (var (attribute, property) in attributes)
        {
            var wanted = attribute.FileOptions?.Variants ?? options.Value.DefaultImageVariants;
            var maxPixels = Math.Min(options.Value.MaxImagePixels, attribute.FileOptions?.MaxPixels ?? long.MaxValue);
            foreach (var image in Images(property.GetValue(entity)))
            {
                images++;
                if (!force && UpToDate(image, wanted))
                    continue;
                if (await RegenerateAsync(processor, id, image, wanted, maxPixels, cancellationToken) is { } result)
                    made[image.FileId] = result;
                else
                    failed++;
            }
        }
        if (made.Count == 0)
            return (images, 0, failed);

        // Phase 2: record them on the entity as it is now, written with the version loaded here (an edit in between fails
        // the save, and the entity is retried).
        using var session = store.OpenAsyncSession();
        var current = await session.LoadAsync<T>(id, cancellationToken);
        var applied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, property) in attributes)
        {
            foreach (var image in Images(current is null ? null : property.GetValue(current)))
            {
                if (!made.TryGetValue(image.FileId, out var result))
                    continue;
                image.Variants = result.Variants;
                if (image.Width <= 0 || image.Height <= 0)
                    (image.Width, image.Height) = (result.Width, result.Height);
                applied.Add(image.FileId);
            }
        }
        if (current is not null && applied.Count > 0)
        {
            await session.StoreAsync(current, session.Advanced.GetChangeVectorFor(current), id, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }

        // Variants no longer configured, and the fresh ones of an image removed meanwhile, are held by nothing now.
        foreach (var (fileId, result) in made)
        {
            var orphans = applied.Contains(fileId) ? result.Obsolete : result.Variants.Values.Select(v => v.FileId).Concat(result.Obsolete);
            foreach (var orphan in orphans)
                await fileStore.DeleteAsync(fileStore.EntityKey(id, orphan), CancellationToken.None);
        }
        return (images, applied.Count, failed + made.Count - applied.Count);
    }

    /// <summary>One image's new variants, its size, and the variant file ids it no longer wants.</summary>
    private sealed record Made(Dictionary<string, SparkImageVariant> Variants, int Width, int Height, List<string> Obsolete);

    /// <summary>Makes and writes <paramref name="image"/>'s variants again, without touching <paramref name="image"/>; null on failure.</summary>
    private async Task<Made?> RegenerateAsync(
        IImageProcessor processor, string entityId, SparkImage image, IReadOnlyDictionary<string, int> wanted, long maxPixels, CancellationToken cancellationToken)
    {
        await using var original = await fileStore.OpenReadAsync(fileStore.EntityKey(entityId, image.FileId), cancellationToken);
        if (original is null)
        {
            logger.LogWarning("Image {FileId} of {Id} has no stored original; its variants were not regenerated.", image.FileId, entityId);
            return null;
        }
        await using var source = await SeekableSpool.SpoolAsync(original, cancellationToken);
        ProcessedImage processed;
        try
        {
            processed = await processor.ProcessAsync(source, new ImageProcessingRequest(image.ContentType, maxPixels, wanted), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Image {FileId} of {Id} could not be processed; its variants were not regenerated.", image.FileId, entityId);
            return null;
        }

        await using (processed)
        {
            var obsolete = image.Variants.Where(v => !wanted.ContainsKey(v.Key)).Select(v => v.Value.FileId).Where(f => f.Length > 0).ToList();
            var variants = new Dictionary<string, SparkImageVariant>(StringComparer.Ordinal);
            foreach (var variant in processed.Variants)
            {
                var fileId = SparkFileKeys.VariantFileId(image.FileId, variant.Name);
                var counting = new HashingReadStream(variant.Content, hash: null);
                await fileStore.WriteAsync(fileStore.EntityKey(entityId, fileId), counting, variant.ContentType, cancellationToken);
                variants[variant.Name] = new SparkImageVariant
                {
                    FileId = fileId,
                    ContentType = variant.ContentType,
                    Width = variant.Width,
                    Height = variant.Height,
                    Length = counting.BytesRead,
                };
            }
            return new Made(variants, processed.Width, processed.Height, obsolete);
        }
    }

    /// <summary>Whether the stored variants are exactly the wanted ones, at the sizes the wanted edges give.</summary>
    internal static bool UpToDate(SparkImage image, IReadOnlyDictionary<string, int> wanted)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Variants.Count != wanted.Count)
            return false;
        foreach (var (name, edge) in wanted)
        {
            if (!image.Variants.TryGetValue(name, out var variant) || string.IsNullOrEmpty(variant.FileId))
                return false;
            if ((variant.Width, variant.Height) != ImageProcessing.Fit(image.Width, image.Height, edge))
                return false;
        }
        return true;
    }

    private static IEnumerable<SparkImage> Images(object? value) => value switch
    {
        null => [],
        SparkImage single => [single],
        IEnumerable many => many.OfType<SparkImage>().ToList(),
        _ => [],
    };
}
