using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SeoTester.Tls;

/// <summary>What a server answered to a ClientHello that offered only one legacy version.</summary>
public enum LegacyTlsAnswer
{
    /// <summary>A ServerHello with exactly the offered version: the server accepts it.</summary>
    Accepted,

    /// <summary>An alert, a ServerHello with another version, or the connection closed: refused.</summary>
    Refused,

    /// <summary>Nothing usable came back (timeout, not TLS at all): no conclusion.</summary>
    Inconclusive,
}

/// <summary>
/// Spike S2 (<c>docs/seo_analyzer_PRD.md</c> §1 hosting, research §3.3): whether a server still accepts TLS 1.0 or 1.1,
/// asked with a hand-built ClientHello so the answer does not depend on the local TLS stack. The <c>aspnet</c> image's
/// OpenSSL 3 and current Windows both refuse to <i>speak</i> these versions, so an <c>SslStream</c> failure proves
/// nothing; this sends the bytes itself and stops at the server's first record (no key exchange, no crypto).
/// <list type="bullet">
/// <item>the hello offers only <paramref name="version"/> (record and client version), no <c>supported_versions</c>
/// extension, and the common legacy cipher suites (RSA/ECDHE with AES-CBC and 3DES) plus SNI, EC point formats and
/// supported groups, so a server that would accept a 2010-era browser answers;</item>
/// <item>the answer is read as one TLS record of at most 16 KiB + 5 bytes, under a timeout.</item>
/// </list>
/// </summary>
public static class LegacyTlsProbe
{
    public const ushort Tls10 = 0x0301;
    public const ushort Tls11 = 0x0302;

    private static readonly ushort[] CipherSuites =
    [
        0xC014, 0xC013, 0xC00A, 0xC009, // ECDHE-RSA/ECDSA AES256/128-CBC-SHA
        0x0035, 0x002F, 0x000A,         // RSA AES256/128-CBC-SHA, 3DES
        0x0039, 0x0033,                 // DHE-RSA AES-CBC
        0x00FF,                         // TLS_EMPTY_RENEGOTIATION_INFO_SCSV
    ];

    /// <summary>Sends the hello on <paramref name="stream"/> and classifies the first record that comes back.</summary>
    public static async Task<LegacyTlsAnswer> ProbeAsync(Stream stream, string host, ushort version, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await stream.WriteAsync(BuildClientHello(host, version), cts.Token);
            await stream.FlushAsync(cts.Token);

            var header = new byte[5];
            if (!await ReadExactlyAsync(stream, header, cts.Token))
                return LegacyTlsAnswer.Refused; // closed without a word: refused
            return await Classify(header, stream, version, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return LegacyTlsAnswer.Inconclusive;
        }
        catch (IOException)
        {
            return LegacyTlsAnswer.Refused; // reset by the peer
        }
    }

    private static async Task<LegacyTlsAnswer> Classify(byte[] header, Stream stream, ushort version, CancellationToken cancellationToken)
    {
        var contentType = header[0];
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3));
        if (contentType == 21) // alert
            return LegacyTlsAnswer.Refused;
        if (contentType != 22 || length is < 6 or > 16_384) // not a handshake record
            return LegacyTlsAnswer.Inconclusive;

        var body = new byte[length];
        if (!await ReadExactlyAsync(stream, body, cancellationToken))
            return LegacyTlsAnswer.Inconclusive;
        if (body[0] != 2) // not a ServerHello
            return LegacyTlsAnswer.Inconclusive;
        var serverVersion = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(4));
        return serverVersion == version ? LegacyTlsAnswer.Accepted : LegacyTlsAnswer.Refused;
    }

    /// <summary>The ClientHello record (exposed for tests).</summary>
    public static byte[] BuildClientHello(string host, ushort version)
    {
        var sni = Encoding.ASCII.GetBytes(host);
        var extensions = new List<byte>();
        // server_name
        AddExtension(extensions, 0x0000, Concat(U16(sni.Length + 3), [0], U16(sni.Length), sni));
        // ec_point_formats: uncompressed
        AddExtension(extensions, 0x000B, [1, 0]);
        // supported_groups: x25519, secp256r1, secp384r1
        AddExtension(extensions, 0x000A, Concat(U16(6), U16(0x001D), U16(0x0017), U16(0x0018)));

        var suites = CipherSuites.SelectMany(U16).ToArray();
        var hello = Concat(
            U16(version),
            RandomNumberGenerator.GetBytes(32),
            [0],                                 // no session id
            U16(suites.Length), suites,
            [1, 0],                              // compression: null
            U16(extensions.Count), extensions.ToArray());
        var handshake = Concat([1], U24(hello.Length), hello);
        return Concat([22], U16(Tls10), U16(handshake.Length), handshake);
    }

    private static void AddExtension(List<byte> into, ushort type, byte[] data)
    {
        into.AddRange(U16(type));
        into.AddRange(U16(data.Length));
        into.AddRange(data);
    }

    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];
    private static byte[] U16(ushort value) => U16((int)value);
    private static byte[] U24(int value) => [(byte)(value >> 16), (byte)(value >> 8), (byte)value];
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }
}
