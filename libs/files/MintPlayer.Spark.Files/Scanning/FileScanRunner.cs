using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Files.Staging;
using MintPlayer.Spark.Files.Storage;

namespace MintPlayer.Spark.Files.Scanning;

/// <summary>
/// Runs the registered <see cref="IFileScanner"/> over a staged upload and records its verdict, for the inline commit and
/// the deferred worker alike. Fail-closed: a timeout, an exception or a missing result is a failure, never a pass.
/// </summary>
[Register(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
internal sealed partial class FileScanRunner
{
    internal const string InfectedMessage = "The file was refused by the malware scanner.";
    internal const string FailedMessage = "The malware scan did not complete, so the file can't be used yet. Commit it again to retry the scan.";

    [Inject] private readonly IEnumerable<IFileScanner> scanners;
    [Inject] private readonly IFileStore fileStore;
    [Inject] private readonly IOptions<SparkFilesOptions> options;
    [Inject] private readonly ILogger<FileScanRunner> logger;

    /// <summary>The scanner in use (the last one registered), or null when the app runs without one.</summary>
    public IFileScanner? Scanner => scanners.LastOrDefault();

    public async Task<FileScanResult> ScanAsync(IFileScanner scanner, SparkFileUpload upload, FileScanContext context, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.ScanTimeout);
        try
        {
            await using var stream = await fileStore.OpenReadAsync(upload.StagingKey, timeout.Token);
            if (stream is null)
                return FileScanResult.Failed("The staged file is gone.");
            return await scanner.ScanAsync(stream, context, timeout.Token) ?? FileScanResult.Failed("The scanner returned no result.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FileScanResult.Failed($"The scan took longer than {options.Value.ScanTimeout}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The malware scanner threw for upload {Token}; the file is not served.", upload.Token);
            return FileScanResult.Failed(ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Records <paramref name="result"/> on the upload. Returns true when its staged bytes must be deleted (after the save).</summary>
    public bool Apply(SparkFileUpload upload, FileScanResult result)
    {
        switch (result.Verdict)
        {
            case FileScanVerdict.Clean:
                upload.ScanStatus = SparkFileScanStatus.Clean;
                upload.State = SparkFileUploadState.Committed;
                upload.Error = null;
                return false;

            case FileScanVerdict.Infected:
                // The document stays as the tombstone (until it expires): who uploaded what, and what the scanner found.
                logger.LogWarning("The malware scanner found {Signature} in upload {Token} ({FileName}) by user {UserId}; the bytes are deleted.",
                    result.Detail, upload.Token, upload.FileName, upload.UserId);
                upload.ScanStatus = SparkFileScanStatus.Infected;
                upload.State = SparkFileUploadState.Refused;
                upload.Error = InfectedMessage;
                return true;

            default:
                logger.LogWarning("The malware scan of upload {Token} did not complete: {Reason}. The file is not served until a scan passes.",
                    upload.Token, result.Detail);
                upload.ScanStatus = SparkFileScanStatus.ScanFailed;
                // Inline, the next commit retries it (an Uploaded document is committed again); deferred, the worker's sweep does.
                if (upload.State != SparkFileUploadState.Committed)
                    upload.State = SparkFileUploadState.Uploaded;
                upload.Error = FailedMessage;
                return false;
        }
    }
}
