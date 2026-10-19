using System.Text;
using System.Text.Json;

namespace MintPlayer.Spark.ReferenceData.Releases;

/// <summary>Where release files are read from and written to: a directory, an HTTP origin, embedded resources.</summary>
public interface IReleaseFileStore
{
    /// <summary>The bytes of <paramref name="path"/> (relative, forward slashes), or <see langword="null"/> when absent.</summary>
    Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken);

    Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken);
}

/// <summary>A release directory on disk (Q-R8's file-system default).</summary>
public sealed class DirectoryReleaseFileStore(string root) : IReleaseFileStore
{
    public string Root { get; } = Path.GetFullPath(root);

    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        return File.Exists(full) ? await File.ReadAllBytesAsync(full, cancellationToken) : null;
    }

    public async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes, cancellationToken);
    }

    private string Resolve(string path)
    {
        if (path.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(path))
            throw new ArgumentException($"'{path}' is not a relative release path.", nameof(path));
        var full = Path.GetFullPath(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(Root, StringComparison.OrdinalIgnoreCase) ? full : throw new ArgumentException($"'{path}' leaves the release root.", nameof(path));
    }
}

/// <summary>A release in memory: documents grouped by country and collection.</summary>
public sealed class ReleaseContent
{
    public required ReleaseManifest Manifest { get; init; }

    /// <summary>Every document, keyed by id (case-insensitive).</summary>
    public required IReadOnlyDictionary<string, object> Documents { get; init; }

    /// <summary>Set when <see cref="Documents"/> is a delta from this release, not the full release.</summary>
    public string? DeltaFrom { get; init; }

    public IEnumerable<T> OfType<T>() => Documents.Values.OfType<T>();
}

/// <summary>Writes a release's canonical files and manifest (§4.6).</summary>
public static class ReleaseWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes one full file per (country, collection) and the manifest. Documents must already carry their
    /// <c>Hash</c> and <c>Release</c> stamp. Returns the manifest bytes.
    /// </summary>
    /// <param name="deltas">Per earlier release: the documents whose hash changed since it (written under <c>deltas/{from}/</c>).</param>
    /// <param name="sign">The signer of the manifest bytes (RD-DEF 5); <see langword="null"/> writes no <c>manifest.sig</c>.</param>
    public static async Task<byte[]> WriteAsync(IReleaseFileStore store, ReleaseManifest manifest, IEnumerable<object> documents, CancellationToken cancellationToken,
        IEnumerable<(string From, IEnumerable<object> Documents)>? deltas = null, Func<byte[], byte[]>? sign = null)
    {
        manifest.Files = await WriteFilesAsync(store, string.Empty, documents, cancellationToken);
        manifest.Deltas = [];
        foreach (var (from, changed) in deltas ?? [])
            manifest.Deltas.Add(new ReleaseDelta { From = from, Files = await WriteFilesAsync(store, $"deltas/{from}/", changed, cancellationToken) });

        manifest.Sources = [.. manifest.Sources.OrderBy(s => s.Id, StringComparer.Ordinal)];
        var manifestBytes = ManifestBytes(manifest);
        await store.WriteAsync(ReleaseFormat.ManifestFile, manifestBytes, cancellationToken);
        if (sign is not null)
            await store.WriteAsync(ReleaseFormat.SignatureFile, ReleaseSignatures.Format(sign(manifestBytes)), cancellationToken);
        return manifestBytes;
    }

    /// <summary>One canonical file per (country, collection) under <paramref name="prefix"/>, sorted by id (ordinal).</summary>
    private static async Task<List<ReleaseFile>> WriteFilesAsync(IReleaseFileStore store, string prefix, IEnumerable<object> documents, CancellationToken cancellationToken)
    {
        var files = new List<ReleaseFile>();
        var groups = documents
            .Select(d => (Doc: d, Id: IdOf(d), Collection: ReleaseFormat.CollectionOf(d.GetType())))
            .GroupBy(x => (Country: CountryOfDocument(x.Doc), x.Collection.Name))
            .OrderBy(g => g.Key.Country, StringComparer.Ordinal)
            .ThenBy(g => ReleaseFormat.Collections.ToList().FindIndex(c => c.Name == g.Key.Name));

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
            var text = new StringBuilder();
            foreach (var item in ordered)
                text.Append(ReleaseCanonical.Serialize(item.Doc)).Append('\n');
            var bytes = Utf8.GetBytes(text.ToString());
            var path = $"{prefix}{group.Key.Country}/{group.Key.Name}.ndjson";
            await store.WriteAsync(path, bytes, cancellationToken);
            files.Add(new ReleaseFile
            {
                Path = path,
                Collection = group.Key.Name,
                Country = group.Key.Country,
                Sha256 = ReleaseCanonical.Sha256(bytes),
                Count = ordered.Count,
                Digest = ReleaseCanonical.Digest(ordered.Select(x => (x.Id, HashOf(x.Doc)))),
            });
        }
        return files;
    }

    /// <summary>The file country of a document: its id's country segment, else its <c>Country</c> field, else <c>_</c>.</summary>
    public static string CountryOfDocument(object document) => RefIds.CountryOf(IdOf(document)) ?? CountryField(document) ?? "_";

    /// <summary>The manifest's canonical bytes: indented for review, properties in declaration order, LF.</summary>
    public static byte[] ManifestBytes(ReleaseManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(ReleaseFormat.JsonOptions) { WriteIndented = true });
        return Utf8.GetBytes(json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    internal static string IdOf(object document)
        => document.GetType().GetProperty("Id")?.GetValue(document) as string
           ?? throw new InvalidDataException($"A {document.GetType().Name} without an id cannot be released.");

    internal static string HashOf(object document)
        => document.GetType().GetProperty("Hash")?.GetValue(document) as string ?? ReleaseCanonical.Hash(document);

    private static string? CountryField(object document)
        => document.GetType().GetProperty("Country")?.GetValue(document) is string { Length: > 0 } country ? country.ToLowerInvariant() : null;
}

/// <summary>Reads and verifies a release (§4.6, §4.7): file hashes, per-file digests, and the never-delete rule.</summary>
public static class ReleaseReader
{
    /// <summary>
    /// Reads the manifest and every file it lists, refusing a file whose SHA-256 or digest disagrees with the manifest.
    /// <paramref name="verifySignature"/>, when given, is called with the manifest bytes and the signature bytes
    /// (or <see langword="null"/> when the release is unsigned) and must throw to refuse.
    /// </summary>
    /// <param name="deltaFrom">
    /// The release the reader already holds: when the manifest carries a delta from it, only the delta files are read
    /// (<see cref="ReleaseContent.DeltaFrom"/> is then set); otherwise the full files.
    /// </param>
    public static async Task<ReleaseContent> ReadAsync(IReleaseFileStore store, CancellationToken cancellationToken, Action<byte[], byte[]?>? verifySignature = null, Func<ReleaseFile, bool>? include = null, string? deltaFrom = null)
    {
        var manifest = await ReadManifestAsync(store, cancellationToken, verifySignature);
        var delta = deltaFrom is null ? null : manifest.Deltas.FirstOrDefault(d => string.Equals(d.From, deltaFrom, StringComparison.Ordinal));
        var documents = await ReadFilesAsync(store, delta?.Files ?? manifest.Files, include, cancellationToken);
        return new ReleaseContent { Manifest = manifest, Documents = documents, DeltaFrom = delta?.From };
    }

    /// <summary>The manifest, its signature checked by <paramref name="verifySignature"/> and its schema version by this reader.</summary>
    public static async Task<ReleaseManifest> ReadManifestAsync(IReleaseFileStore store, CancellationToken cancellationToken, Action<byte[], byte[]?>? verifySignature = null)
    {
        var manifestBytes = await store.ReadAsync(ReleaseFormat.ManifestFile, cancellationToken)
            ?? throw new ReleaseRefusedException("The release has no manifest.json.");
        verifySignature?.Invoke(manifestBytes, await store.ReadAsync(ReleaseFormat.SignatureFile, cancellationToken));
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestBytes, ReleaseFormat.JsonOptions)
            ?? throw new ReleaseRefusedException("The manifest is empty.");
        if (manifest.SchemaVersion != ReleaseFormat.SchemaVersion)
            throw new ReleaseRefusedException($"Release schema {manifest.SchemaVersion} is not supported (this reader knows {ReleaseFormat.SchemaVersion}).");
        return manifest;
    }

    /// <summary>The documents of <paramref name="files"/>, each file checked against its SHA-256, count and digest, each document against its own hash.</summary>
    public static async Task<Dictionary<string, object>> ReadFilesAsync(IReleaseFileStore store, IEnumerable<ReleaseFile> files, Func<ReleaseFile, bool>? include, CancellationToken cancellationToken)
    {
        var documents = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(f => include?.Invoke(f) ?? true))
        {
            var collection = ReleaseFormat.CollectionNamed(file.Collection)
                ?? throw new ReleaseRefusedException($"Unknown collection '{file.Collection}' in {file.Path}.");
            var bytes = await store.ReadAsync(file.Path, cancellationToken)
                ?? throw new ReleaseRefusedException($"{file.Path} is listed in the manifest but missing.");
            if (!string.Equals(ReleaseCanonical.Sha256(bytes), file.Sha256, StringComparison.Ordinal))
                throw new ReleaseRefusedException($"{file.Path} does not match its SHA-256 in the manifest.");

            var pairs = new List<(string, string)>();
            foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
            {
                if (line.Length == 0) continue;
                var document = ReleaseCanonical.Deserialize(line, collection);
                var id = ReleaseWriter.IdOf(document);
                var hash = ReleaseWriter.HashOf(document);
                if (!string.Equals(ReleaseCanonical.Hash(document), hash, StringComparison.Ordinal))
                    throw new ReleaseRefusedException($"{id} in {file.Path} does not match its own Hash.");
                pairs.Add((id, hash));
                documents[id] = document;
            }
            if (pairs.Count != file.Count || !string.Equals(ReleaseCanonical.Digest(pairs), file.Digest, StringComparison.Ordinal))
                throw new ReleaseRefusedException($"{file.Path} does not match its count or digest in the manifest.");
        }

        return documents;
    }

    /// <summary>Refuses <paramref name="next"/> when it lacks any id <paramref name="previousIds"/> held (ending is a closed period, never a deletion).</summary>
    public static void RefuseDroppedIds(IEnumerable<string> previousIds, ReleaseContent next)
    {
        var dropped = previousIds.Where(id => !next.Documents.ContainsKey(id)).Take(5).ToList();
        if (dropped.Count > 0)
            throw new ReleaseRefusedException($"Release {next.Manifest.Release} drops ids it may never drop: {string.Join(", ", dropped)}.");
    }
}
