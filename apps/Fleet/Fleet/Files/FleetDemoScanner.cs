using System.Text;
using MintPlayer.Spark.Files.Scanning;

namespace Fleet.Files;

/// <summary>
/// ⚠️ Demo only: a "scanner" that knows exactly one signature, the EICAR test file, so the demo and its browser tests can
/// show an infected upload being refused without running ClamAV. It is <b>not</b> antivirus. A real app registers
/// <c>files.UseClamAV(…)</c> (MintPlayer.Spark.Files.ClamAV) instead; see docs/guide-file-attributes.md.
/// </summary>
/// <remarks>
/// Registering a scanner is also what lets Fleet start outside Development (the E2E host runs as "E2E"): with no scanner,
/// AddFiles() refuses to start there unless <c>Spark:Files:AllowUnscannedFiles</c> is set (#700 D5).
/// </remarks>
internal sealed class FleetDemoScanner : IFileScanner
{
    /// <summary>The 68-byte EICAR anti-malware test string's distinctive part.</summary>
    private static readonly byte[] Eicar = Encoding.ASCII.GetBytes("EICAR-STANDARD-ANTIVIRUS-TEST-FILE");

    public async Task<FileScanResult> ScanAsync(Stream content, FileScanContext context, CancellationToken cancellationToken)
    {
        // The EICAR file is 68 bytes and may be followed by whitespace only; reading the first 4 KiB is plenty.
        var buffer = new byte[4096];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await content.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
            read += n;
        return buffer.AsSpan(0, read).IndexOf(Eicar) >= 0
            ? FileScanResult.Infected("EICAR-Test-File")
            : FileScanResult.Clean;
    }
}
