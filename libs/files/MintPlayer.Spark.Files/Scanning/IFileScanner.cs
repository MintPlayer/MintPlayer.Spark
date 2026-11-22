using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Files.Scanning;

/// <summary>
/// A malware scanner (#700 §3.4). Register one with <c>files.UseScanner&lt;T&gt;()</c>, or a provider's own extension
/// (<c>files.UseClamAV(…)</c>). It runs at commit, after the size check, the sniff and the upload handlers, and only a
/// <see cref="FileScanVerdict.Clean"/> file becomes claimable. Fail-closed: anything but a clean verdict, an exception
/// included, keeps the file from ever being served.
/// </summary>
public interface IFileScanner
{
    /// <summary>
    /// Scans <paramref name="content"/> (the staged bytes, read from the start). Return <see cref="FileScanResult.Failed"/>
    /// rather than <see cref="FileScanResult.Clean"/> whenever the scan did not run to a verdict. A thrown exception counts
    /// as a failure too.
    /// </summary>
    Task<FileScanResult> ScanAsync(Stream content, FileScanContext context, CancellationToken cancellationToken);
}

/// <summary>What is being scanned: the attribute it is for and what the commit already measured.</summary>
public sealed record FileScanContext(
    EntityTypeDefinition EntityType,
    EntityAttributeDefinition Attribute,
    string UserId,
    string FileName,
    string ContentType,
    long Length,
    string Sha256);

public enum FileScanVerdict
{
    /// <summary>The scanner ran and found nothing it knows.</summary>
    Clean,

    /// <summary>The scanner found malware, or a limit it alerts on. The bytes are deleted.</summary>
    Infected,

    /// <summary>No verdict: unreachable, a timeout, a reset, an unreadable reply. The file is not served; a commit retries it.</summary>
    Failed,
}

/// <summary>A scanner's answer. <see cref="Detail"/> is for the log (a signature, a reason), never for the uploader.</summary>
public sealed record FileScanResult(FileScanVerdict Verdict, string? Detail)
{
    public static FileScanResult Clean { get; } = new(FileScanVerdict.Clean, null);

    public static FileScanResult Infected(string signature) => new(FileScanVerdict.Infected, signature);

    public static FileScanResult Failed(string reason) => new(FileScanVerdict.Failed, reason);
}

/// <summary>When the scan runs (D6).</summary>
public enum SparkFileScanMode
{
    /// <summary>During the commit request: the token comes back already <c>Clean</c> (or refused).</summary>
    Inline,

    /// <summary>
    /// After the commit: it answers <c>Pending</c> at once and a background worker scans. The client commits the token again
    /// to read the verdict; a save can claim it only once it is <c>Clean</c>.
    /// </summary>
    Deferred,
}
