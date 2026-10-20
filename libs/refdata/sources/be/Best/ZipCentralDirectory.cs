using System.Buffers.Binary;
using System.Text;

namespace MintPlayer.Spark.ReferenceData.Sources.Belgium.Best;

/// <summary>One entry of a ZIP central directory: enough to fetch it by range request and to check its CRC.</summary>
public sealed record ZipDirectoryEntry(string Name, ushort Method, uint Crc32, long CompressedSize, long UncompressedSize, long LocalHeaderOffset);

/// <summary>Where a ZIP's central directory lies (from the end-of-central-directory record, ZIP64 aware).</summary>
public sealed record ZipDirectoryLocation(long Offset, long Size, long Count);

/// <summary>
/// Reads a ZIP's central directory from the bytes at the end of the file (PRD §4.4: the BeST bundle is 300+ MB, its
/// non-address entries ≈3 MB, so the change check reads the last 64 KB by range request and compares the inner
/// entries' CRCs). Handles ZIP64 (the EOCD's fields saturated at <c>0xFFFF</c>/<c>0xFFFFFFFF</c>, the ZIP64 locator
/// and record, and the ZIP64 extra field of each entry). Downloads are data: every offset and length is bounds-checked.
/// </summary>
public static class ZipCentralDirectory
{
    private const uint EocdSignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint Zip64EocdSignature = 0x06064b50;
    private const uint CentralSignature = 0x02014b50;
    private const uint LocalSignature = 0x04034b50;

    /// <summary>The tail size the change check requests: the EOCD comment is at most 64 KB − 1.</summary>
    public const int TailBytes = 65536;

    /// <summary>
    /// Locates the central directory. <paramref name="tail"/> holds the last bytes of a file of
    /// <paramref name="fileLength"/> bytes.
    /// </summary>
    public static ZipDirectoryLocation Locate(ReadOnlySpan<byte> tail, long fileLength)
    {
        var eocd = LastIndexOf(tail, EocdSignature, minimumRemaining: 22);
        if (eocd < 0)
            throw new InvalidDataException("No end-of-central-directory record in the last bytes: not a ZIP, or the comment is too long.");
        long count = BinaryPrimitives.ReadUInt16LittleEndian(tail[(eocd + 10)..]);
        long size = BinaryPrimitives.ReadUInt32LittleEndian(tail[(eocd + 12)..]);
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(tail[(eocd + 16)..]);

        if (count == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF)
        {
            // ZIP64: the locator sits right before the EOCD and points at the ZIP64 EOCD record.
            var locator = eocd - 20;
            if (locator < 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail[locator..]) != Zip64LocatorSignature)
                throw new InvalidDataException("ZIP64 fields without a ZIP64 locator.");
            var recordOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail[(locator + 8)..]);
            var tailStart = fileLength - tail.Length;
            var record = recordOffset - tailStart;
            if (record < 0 || record + 56 > tail.Length || BinaryPrimitives.ReadUInt32LittleEndian(tail[(int)record..]) != Zip64EocdSignature)
                throw new InvalidDataException("The ZIP64 end-of-central-directory record is outside the bytes read.");
            var r = (int)record;
            count = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(tail[(r + 32)..]));
            size = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(tail[(r + 40)..]));
            offset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(tail[(r + 48)..]));
        }

        if (offset < 0 || size < 0 || offset + size > fileLength)
            throw new InvalidDataException($"The central directory ({offset}+{size}) lies outside the {fileLength}-byte file.");
        return new ZipDirectoryLocation(offset, size, count);
    }

    /// <summary>The entries of a central directory (<paramref name="directory"/> = exactly its bytes).</summary>
    public static IReadOnlyList<ZipDirectoryEntry> ReadEntries(ReadOnlySpan<byte> directory, long count)
    {
        var entries = new List<ZipDirectoryEntry>();
        var p = 0;
        for (long i = 0; i < count; i++)
        {
            if (p + 46 > directory.Length || BinaryPrimitives.ReadUInt32LittleEndian(directory[p..]) != CentralSignature)
                throw new InvalidDataException($"Central-directory entry {i} is truncated or malformed.");
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(directory[(p + 8)..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(directory[(p + 10)..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(directory[(p + 16)..]);
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(directory[(p + 20)..]);
            long uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(directory[(p + 24)..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(directory[(p + 28)..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(directory[(p + 30)..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(directory[(p + 32)..]);
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(directory[(p + 42)..]);
            var end = p + 46 + nameLength + extraLength + commentLength;
            if (end > directory.Length)
                throw new InvalidDataException($"Central-directory entry {i} runs past the directory.");
            var nameBytes = directory.Slice(p + 46, nameLength);
            // Bit 11: UTF-8 names; otherwise CP437, whose ASCII range is all BeST uses.
            var name = (flags & 0x0800) != 0 ? Encoding.UTF8.GetString(nameBytes) : Encoding.Latin1.GetString(nameBytes);

            var extra = directory.Slice(p + 46 + nameLength, extraLength);
            while (extra.Length >= 4)
            {
                var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
                if (4 + length > extra.Length) break;
                if (id == 0x0001)
                {
                    // The ZIP64 extra holds, in this order, only the fields that were saturated.
                    var field = extra.Slice(4, length);
                    if (uncompressed == 0xFFFFFFFF && field.Length >= 8) { uncompressed = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(field)); field = field[8..]; }
                    if (compressed == 0xFFFFFFFF && field.Length >= 8) { compressed = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(field)); field = field[8..]; }
                    if (localOffset == 0xFFFFFFFF && field.Length >= 8) { localOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(field)); }
                }
                extra = extra[(4 + length)..];
            }

            entries.Add(new ZipDirectoryEntry(name, method, crc, compressed, uncompressed, localOffset));
            p = end;
        }
        return entries;
    }

    /// <summary>The length of a local file header (30 bytes plus its own name and extra), read from its first 30 bytes.</summary>
    public static int LocalHeaderLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < 30 || BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalSignature)
            throw new InvalidDataException("Not a ZIP local file header.");
        return 30 + BinaryPrimitives.ReadUInt16LittleEndian(header[26..]) + BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
    }

    /// <summary>A digest of the entries' names and CRCs: the bundle's version as far as the selected entries go.</summary>
    public static string Fingerprint(IEnumerable<ZipDirectoryEntry> entries)
    {
        var text = string.Concat(entries.OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => $"{e.Name}:{e.Crc32:x8}:{e.UncompressedSize}\n"));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    private static int LastIndexOf(ReadOnlySpan<byte> data, uint signature, int minimumRemaining)
    {
        for (var i = data.Length - minimumRemaining; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) == signature)
                return i;
        return -1;
    }
}

/// <summary>CRC-32 (IEEE 802.3, the ZIP polynomial), so a range-fetched entry is checked against the central directory.</summary>
public static class ZipCrc32
{
    private static readonly uint[] Table = Build();

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
