using MintPlayer.Spark.Files.Staging;

namespace MintPlayer.Spark.Files.Storage;

/// <summary>How image variants are named in a store (#700 M8), the same on every store.</summary>
public static class SparkFileKeys
{
    /// <summary>The file id of <paramref name="fileId"/>'s variant <paramref name="variant"/>: <c>{fileId}.{variant}</c>.</summary>
    public static string VariantFileId(string fileId, string variant)
        => IsVariantName(variant) ? fileId + "." + variant : throw new ArgumentException("Not a variant name.", nameof(variant));

    /// <summary>A variant name a key can carry: 1–64 ASCII letters, digits, <c>-</c> and <c>_</c> (the model's own rule, capped).</summary>
    public static bool IsVariantName(string? variant)
        => variant is { Length: > 0 and <= 64 } && variant.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Every key an upload has staged: the original and each variant. A refusal deletes them all.</summary>
    public static IEnumerable<string> StagedKeys(this IFileStore store, SparkFileUpload upload)
    {
        yield return upload.StagingKey;
        foreach (var variant in upload.Variants?.Keys ?? Enumerable.Empty<string>())
            yield return store.StagingVariantKey(upload.Token, variant);
    }
}
