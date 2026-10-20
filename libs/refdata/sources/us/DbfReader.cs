using System.Buffers.Binary;
using System.Text;

namespace MintPlayer.Spark.ReferenceData.Sources.UnitedStates;

/// <summary>
/// A small streaming dBASE III reader for a shapefile's attribute table (<c>.dbf</c>): the header, the field
/// descriptors up to the <c>0x0D</c> terminator, then fixed-length records read one at a time. Deleted records
/// (<c>*</c>) are skipped; values are trimmed. Only character, numeric, date and logical fields occur in the files read
/// here; every value is returned as text. The encoding is the <c>.cpg</c>'s (TIGER/Line states UTF-8).
/// </summary>
internal static class DbfReader
{
    private const int MaxRecordLength = 64 * 1024;

    public static IEnumerable<IReadOnlyDictionary<string, string>> Read(Stream stream, Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        var header = new byte[32];
        ReadExactly(stream, header);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
        var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
        if (recordLength == 0 || recordLength > MaxRecordLength || headerLength < 33)
            throw new InvalidDataException($"Not a dBASE table: header {headerLength} bytes, records {recordLength} bytes.");

        var descriptors = new byte[headerLength - 32];
        ReadExactly(stream, descriptors);
        var fields = new List<(string Name, int Length)>();
        for (var at = 0; at + 32 <= descriptors.Length && descriptors[at] != 0x0D; at += 32)
        {
            var name = Encoding.ASCII.GetString(descriptors, at, 11).TrimEnd('\0', ' ');
            fields.Add((name, descriptors[at + 16]));
        }
        if (fields.Sum(f => f.Length) + 1 != recordLength)
            throw new InvalidDataException($"The dBASE field lengths ({fields.Sum(f => f.Length)} + 1) do not add up to the record length {recordLength}.");

        var record = new byte[recordLength];
        for (var i = 0u; i < count; i++)
        {
            if (!TryReadExactly(stream, record) || record[0] == 0x1A)
                yield break;
            if (record[0] == (byte)'*')
                continue;
            var values = new Dictionary<string, string>(fields.Count, StringComparer.OrdinalIgnoreCase);
            var offset = 1;
            foreach (var (name, length) in fields)
            {
                values[name] = encoding.GetString(record, offset, length).Trim(' ', '\0');
                offset += length;
            }
            yield return values;
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        if (!TryReadExactly(stream, buffer))
            throw new InvalidDataException("The dBASE table ends early.");
    }

    private static bool TryReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
