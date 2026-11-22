namespace MintPlayer.Spark.Files.Sniffing;

/// <summary>
/// A file format recognised by its magic bytes (#700 D4). Every type an attribute accepts must have one: a type that
/// can't be recognised from its content can't be accepted, because the client's extension and MIME type are claims.
/// Register more with <c>files.AddSignature(…)</c>.
/// </summary>
public interface ISparkFileSignature
{
    /// <summary>A short name for messages and logs, e.g. <c>PNG</c>.</summary>
    string Name { get; }

    /// <summary>
    /// The extensions (lower case, with the dot) a file of this format may carry, each with the content type it is
    /// stored and served as. A file whose name has another extension is refused (an extension/content mismatch).
    /// </summary>
    IReadOnlyDictionary<string, string> Extensions { get; }

    /// <summary>How many leading bytes <see cref="Matches"/> needs. At most <see cref="FileSniffer.HeaderLength"/>.</summary>
    int HeaderBytes { get; }

    /// <summary>Whether <paramref name="header"/> (the file's first bytes, possibly fewer than asked) is this format.</summary>
    bool Matches(ReadOnlySpan<byte> header);
}

/// <summary>A signature given by fixed bytes at an offset, the shape almost every format has.</summary>
public sealed class MagicBytesSignature : ISparkFileSignature
{
    private readonly (int Offset, byte[] Bytes)[] parts;

    public MagicBytesSignature(string name, IReadOnlyDictionary<string, string> extensions, params (int Offset, byte[] Bytes)[] parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (extensions.Count == 0)
            throw new ArgumentException("A signature needs at least one extension.", nameof(extensions));
        if (parts.Length == 0 || parts.Any(p => p.Bytes.Length == 0))
            throw new ArgumentException("A signature needs magic bytes.", nameof(parts));
        Name = name;
        Extensions = extensions.ToDictionary(e => e.Key.ToLowerInvariant(), e => e.Value, StringComparer.OrdinalIgnoreCase);
        this.parts = parts;
        HeaderBytes = parts.Max(p => p.Offset + p.Bytes.Length);
    }

    public string Name { get; }
    public IReadOnlyDictionary<string, string> Extensions { get; }
    public int HeaderBytes { get; }

    public bool Matches(ReadOnlySpan<byte> header)
    {
        foreach (var (offset, bytes) in parts)
        {
            if (header.Length < offset + bytes.Length || !header.Slice(offset, bytes.Length).SequenceEqual(bytes))
                return false;
        }
        return true;
    }
}
