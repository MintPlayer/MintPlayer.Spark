using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace MintPlayer.Spark.Tests.Files.Images;

/// <summary>
/// Images built at test time (PRD SP5: no personal data, nothing checked in). The quadrant image has four colours, so every
/// one of the eight EXIF orientations lands it differently.
/// </summary>
internal static class ImageFixtures
{
    public static readonly SKColor Red = new(220, 20, 20), Green = new(20, 200, 20), Blue = new(20, 20, 220), White = new(240, 240, 240);

    /// <summary>A <paramref name="width"/>×<paramref name="height"/> bitmap: top-left red, top-right green, bottom-left blue, bottom-right white.</summary>
    public static SKBitmap Quadrants(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        int hw = width / 2, hh = height / 2;
        using var paint = new SKPaint();
        foreach (var (color, rect) in new[]
        {
            (Red, new SKRect(0, 0, hw, hh)), (Green, new SKRect(hw, 0, width, hh)),
            (Blue, new SKRect(0, hh, hw, height)), (White, new SKRect(hw, hh, width, height)),
        })
        {
            paint.Color = color;
            canvas.DrawRect(rect, paint);
        }
        canvas.Flush();
        return bitmap;
    }

    public static byte[] Png(int width, int height)
    {
        using var bitmap = Quadrants(width, height);
        return Encode(bitmap, SKEncodedImageFormat.Png, 100);
    }

    public static byte[] Jpeg(int width, int height)
    {
        using var bitmap = Quadrants(width, height);
        return Encode(bitmap, SKEncodedImageFormat.Jpeg, 95);
    }

    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var data = bitmap.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>
    /// <paramref name="jpeg"/> with an EXIF APP1 segment right after SOI: IFD0 holding <c>Orientation</c> and, when given,
    /// an <c>Artist</c> string (the marker the metadata tests look for) and a GPS IFD pointer with a latitude.
    /// </summary>
    public static byte[] WithExif(byte[] jpeg, ushort orientation, string? artist = null, bool gps = false)
    {
        var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>
        {
            (0x0112, 3, 1, U16(orientation)),
        };
        if (artist is not null)
            entries.Add((0x013B, 2, (uint)(artist.Length + 1), Encoding.ASCII.GetBytes(artist + "\0")));

        // Layout: TIFF header (8), IFD0, [GPS IFD], data area.
        var ifd0Size = 2 + (entries.Count + (gps ? 1 : 0)) * 12 + 4;
        var gpsIfdOffset = 8 + ifd0Size;
        const int gpsIfdSize = 2 + 2 * 12 + 4;
        var dataOffset = gpsIfdOffset + (gps ? gpsIfdSize : 0);

        using var tiff = new MemoryStream();
        var data = new MemoryStream();
        tiff.Write("II"u8);
        tiff.Write(U16(42));
        tiff.Write(U32(8));

        void WriteIfd(List<(ushort Tag, ushort Type, uint Count, byte[] Value)> list)
        {
            tiff.Write(U16((ushort)list.Count));
            foreach (var (tag, type, count, value) in list.OrderBy(e => e.Tag))
            {
                tiff.Write(U16(tag));
                tiff.Write(U16(type));
                tiff.Write(U32(count));
                if (value.Length <= 4)
                    tiff.Write(value.Concat(new byte[4 - value.Length]).ToArray());
                else
                {
                    tiff.Write(U32((uint)(dataOffset + data.Length)));
                    data.Write(value);
                }
            }
            tiff.Write(U32(0));
        }

        if (gps)
            entries.Add((0x8825, 4, 1, U32((uint)gpsIfdOffset)));
        WriteIfd(entries);
        if (gps)
        {
            // GPSLatitudeRef "N", GPSLatitude 51/1 2/1 3/1 (rationals in the data area).
            var latitude = U32(51).Concat(U32(1)).Concat(U32(2)).Concat(U32(1)).Concat(U32(3)).Concat(U32(1)).ToArray();
            WriteIfd([(0x0001, 2, 2, "N\0"u8.ToArray()), (0x0002, 5, 3, latitude)]);
        }
        tiff.Write(data.ToArray());

        var payload = "Exif\0\0"u8.ToArray().Concat(tiff.ToArray()).ToArray();
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(2 + payload.Length));
        payload.CopyTo(segment, 4);
        return jpeg[..2].Concat(segment).Concat(jpeg[2..]).ToArray();
    }

    /// <summary>A 2×2 animated GIF: frame 0 all red, frame 1 all blue. LZW with a clear code before every pixel, so it stays 3-bit.</summary>
    public static byte[] AnimatedGif()
    {
        using var gif = new MemoryStream();
        gif.Write("GIF89a"u8);
        gif.Write(U16(2));
        gif.Write(U16(2));
        gif.Write([0x81, 0x00, 0x00]); // a global colour table of 4 entries
        gif.Write([220, 20, 20, 20, 20, 220, 0, 0, 0, 255, 255, 255]);
        gif.Write([0x21, 0xFF, 0x0B]);
        gif.Write("NETSCAPE2.0"u8);
        gif.Write([0x03, 0x01, 0x00, 0x00, 0x00]);
        foreach (var colourIndex in new[] { 0, 1 })
        {
            gif.Write([0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00]);
            gif.Write([0x2C, 0, 0, 0, 0, 2, 0, 2, 0, 0x00]);
            gif.WriteByte(0x02); // LZW minimum code size: clear = 4, end = 5, codes of 3 bits
            var codes = new List<int>();
            for (var i = 0; i < 4; i++)
            {
                codes.Add(4);
                codes.Add(colourIndex);
            }
            codes.Add(5);
            var packed = Pack(codes, 3);
            gif.WriteByte((byte)packed.Length);
            gif.Write(packed);
            gif.WriteByte(0x00);
        }
        gif.WriteByte(0x3B);
        return gif.ToArray();
    }

    private static byte[] Pack(List<int> codes, int bits)
    {
        var output = new List<byte>();
        int buffer = 0, filled = 0;
        foreach (var code in codes)
        {
            buffer |= code << filled;
            filled += bits;
            while (filled >= 8)
            {
                output.Add((byte)(buffer & 0xFF));
                buffer >>= 8;
                filled -= 8;
            }
        }
        if (filled > 0)
            output.Add((byte)buffer);
        return [.. output];
    }

    private static readonly Lazy<byte[]> bomb = new(BuildBomb);

    /// <summary>A 30000×30000 1-bit PNG (900 Mpx) of about 100 KB: a decompression bomb, real down to its CRCs.</summary>
    public static byte[] Bomb => bomb.Value;

    private static byte[] BuildBomb()
    {
        const int side = 30000;
        var row = new byte[1 + side / 8]; // filter byte + 3750 bytes of zero bits
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            for (var y = 0; y < side; y++)
                zlib.Write(row);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, side);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), side);
        header[8] = 1; // bit depth
        header[9] = 0; // greyscale
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(U32BigEndian((uint)data.Length));
        png.Write(typeBytes);
        png.Write(data);
        png.Write(U32BigEndian(Crc32(typeBytes.Concat(data).ToArray())));
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static byte[] U16(ushort value) => BitConverter.GetBytes(value);

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);

    private static byte[] U32BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>Whether <paramref name="haystack"/> holds the ASCII text <paramref name="needle"/>.</summary>
    public static bool Contains(byte[] haystack, string needle) => haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(needle)) >= 0;
}
