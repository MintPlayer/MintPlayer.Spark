using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Files;
using MintPlayer.Spark.Files.Scanning;
using MintPlayer.Spark.Files.Staging;

namespace MintPlayer.Spark.Tests.Files;

/// <summary>
/// #700 §3.4, D5, D6: the scanner runs last at commit; only a clean verdict makes a token claimable; every other outcome
/// (infected, failed, thrown, timed out) is fail-closed. Deferred scanning answers Pending until the worker's verdict.
/// </summary>
public class FileScanningTests : FilesTestHost
{
    private const string Alice = "users/alice";

    private readonly FakeScanner scanner = new();

    public FileScanningTests() => ConfigureFiles = files => files.UseScanner(_ => scanner);

    private async Task<string> UploadOnlyAsync(string fixture = "scan.pdf")
    {
        var token = await PrepareOneAsync(Alice, "Document", fixture);
        using var upload = await UploadAsync(Alice, token, fixture, FileSnifferTests.Fixture(fixture));
        upload.StatusCode.Should().Be(HttpStatusCode.NoContent);
        return token;
    }

    private async Task<HttpStatusCode> ClaimAsync(string token)
    {
        var (status, _) = await SaveCarAsync(Alice, null, ("Name", "Golf"), ("Document", new { uploadToken = token }));
        return status;
    }

    [Fact]
    public async Task A_clean_verdict_commits_the_file_and_the_scanner_saw_its_bytes_and_context()
    {
        var token = await UploadOnlyAsync();

        var (status, body) = await CommitAsync(Alice, token);

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var staged = (await StagedAsync(token))!;
        staged.State.Should().Be(SparkFileUploadState.Committed);
        staged.ScanStatus.Should().Be(SparkFileScanStatus.Clean);
        scanner.Scanned.Should().ContainSingle().Which.Should().Equal(FileSnifferTests.Fixture("scan.pdf"));
        var context = scanner.Contexts.Single();
        context.Attribute.Name.Should().Be("Document");
        context.EntityType.Name.Should().Be("FiCar");
        context.UserId.Should().Be(Alice);
        context.ContentType.Should().Be("application/pdf");
        context.Length.Should().Be(FileSnifferTests.Fixture("scan.pdf").Length);
        context.Sha256.Should().Be(staged.Sha256);
        (await ClaimAsync(token)).Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task An_infected_file_is_refused_its_bytes_deleted_and_the_document_kept_as_a_tombstone()
    {
        scanner.Next = _ => Task.FromResult(FileScanResult.Infected("Eicar-Test-Signature"));
        var token = await UploadOnlyAsync();

        var (_, body) = await CommitAsync(Alice, token);

        body.GetProperty("files")[0].GetProperty("error").GetString().Should().Be("The file was refused by the malware scanner.",
            "the signature is for the log, not the uploader");
        var staged = (await StagedAsync(token))!;
        staged.State.Should().Be(SparkFileUploadState.Refused);
        staged.ScanStatus.Should().Be(SparkFileScanStatus.Infected);
        (await StagedAttachmentCountAsync(token)).Should().Be(0);
        (await ClaimAsync(token)).Should().Be(HttpStatusCode.BadRequest);
    }

    public static TheoryData<string> Failures => ["failed", "throws", "times out", "returns null"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_scan_without_a_verdict_is_never_a_pass_and_committing_again_retries_it(string failure)
    {
        ConfigureFiles += files => files.ConfigureOptions(o => o.ScanTimeout = TimeSpan.FromMilliseconds(500));
        scanner.Next = failure switch
        {
            "failed" => _ => Task.FromResult(FileScanResult.Failed("connection reset")),
            "throws" => _ => throw new IOException("clamd went away"),
            "times out" => async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return FileScanResult.Clean; },
            _ => _ => Task.FromResult<FileScanResult>(null!),
        };
        var token = await UploadOnlyAsync();

        var (_, body) = await CommitAsync(Alice, token);

        body.GetProperty("files")[0].GetProperty("error").GetString().Should().Contain("malware scan did not complete");
        var staged = (await StagedAsync(token))!;
        staged.ScanStatus.Should().Be(SparkFileScanStatus.ScanFailed);
        staged.State.Should().Be(SparkFileUploadState.Uploaded);
        (await StagedAttachmentCountAsync(token)).Should().Be(1, "a failed scan keeps the bytes for the retry");
        (await ClaimAsync(token)).Should().Be(HttpStatusCode.BadRequest);

        scanner.Next = null;
        var (_, retried) = await CommitAsync(Alice, token);

        retried.GetProperty("files")[0].GetProperty("error").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        (await StagedAsync(token))!.ScanStatus.Should().Be(SparkFileScanStatus.Clean);
        (await ClaimAsync(token)).Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task An_upload_handler_refusal_comes_before_the_scan()
    {
        ConfigureFiles += files => files.AddUploadHandler<RefuseEverything>();
        var token = await UploadOnlyAsync();

        await CommitAsync(Alice, token);

        (await StagedAsync(token))!.State.Should().Be(SparkFileUploadState.Refused);
        scanner.Scanned.Should().BeEmpty();
    }

    [Fact]
    public async Task Deferred_answers_pending_and_the_token_is_claimable_only_after_the_worker_found_it_clean()
    {
        ConfigureFiles += files => files.ConfigureOptions(o => o.ScanMode = SparkFileScanMode.Deferred);
        var gate = new TaskCompletionSource();
        scanner.Next = async _ => { await gate.Task.WaitAsync(TimeSpan.FromSeconds(30)); return FileScanResult.Clean; };
        var token = await UploadOnlyAsync();

        var (status, _) = await CommitAsync(Alice, token);

        status.Should().Be(HttpStatusCode.OK);
        var staged = (await StagedAsync(token))!;
        staged.State.Should().Be(SparkFileUploadState.Committed);
        staged.ScanStatus.Should().Be(SparkFileScanStatus.Pending);
        (await ClaimAsync(token)).Should().Be(HttpStatusCode.BadRequest, "a pending file is not claimable");

        gate.SetResult();
        await WaitForScanAsync(token, SparkFileScanStatus.Clean);
        (await ClaimAsync(token)).Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Deferred_infected_deletes_the_bytes_and_refuses_the_token()
    {
        ConfigureFiles += files => files.ConfigureOptions(o => o.ScanMode = SparkFileScanMode.Deferred);
        scanner.Next = _ => Task.FromResult(FileScanResult.Infected("Eicar-Test-Signature"));
        var token = await UploadOnlyAsync();

        await CommitAsync(Alice, token);
        await WaitForScanAsync(token, SparkFileScanStatus.Infected);

        (await StagedAsync(token))!.State.Should().Be(SparkFileUploadState.Refused);
        (await StagedAttachmentCountAsync(token)).Should().Be(0);
        (await ClaimAsync(token)).Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_deferred_sweep_picks_up_what_the_queue_lost_and_retries_a_failed_scan()
    {
        ConfigureFiles += files => files.ConfigureOptions(o => o.ScanMode = SparkFileScanMode.Deferred);
        scanner.Next = _ => Task.FromResult(FileScanResult.Failed("clamd is down"));
        var token = await UploadOnlyAsync();
        await CommitAsync(Alice, token);
        await WaitForScanAsync(token, SparkFileScanStatus.ScanFailed);
        (await StagedAsync(token))!.State.Should().Be(SparkFileUploadState.Committed, "deferred, a failure stays committed for the sweep");

        scanner.Next = null;
        var deferred = Factory.GetService<DeferredFileScanner>();
        (await deferred.SweepAsync(CancellationToken.None)).Should().Be(1);
        await WaitForScanAsync(token, SparkFileScanStatus.Clean);
    }

    private async Task WaitForScanAsync(string token, SparkFileScanStatus expected)
    {
        // A failure bound, not a pace: the worker usually answers within milliseconds.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        SparkFileScanStatus? actual = null;
        while (DateTime.UtcNow < deadline)
        {
            actual = (await StagedAsync(token))?.ScanStatus;
            if (actual == expected)
                return;
            await Task.Delay(50);
        }
        actual.Should().Be(expected, "the deferred worker records its verdict");
    }

    [Fact]
    public void Outside_Development_without_a_scanner_the_app_refuses_to_start_unless_it_allows_unscanned_files()
    {
        SparkFilesExtensions.UnscannedProblem(new SparkFilesOptions(), hasScanner: false, isDevelopment: false)
            .Should().Contain("No IFileScanner").And.Contain("AllowUnscannedFiles");
        SparkFilesExtensions.UnscannedProblem(new SparkFilesOptions { AllowUnscannedFiles = true }, hasScanner: false, isDevelopment: false).Should().BeNull();
        SparkFilesExtensions.UnscannedProblem(new SparkFilesOptions(), hasScanner: false, isDevelopment: true).Should().BeNull();
        SparkFilesExtensions.UnscannedProblem(new SparkFilesOptions(), hasScanner: true, isDevelopment: false).Should().BeNull();
    }

    public sealed class RefuseEverything : IFileUploadHandler
    {
        public ValueTask<FileUploadDecision> ValidateAsync(FileUploadContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(FileUploadDecision.Refuse("Not today."));
    }

    /// <summary>Clean unless <see cref="Next"/> says otherwise; records what it was given.</summary>
    internal sealed class FakeScanner : IFileScanner
    {
        public Func<CancellationToken, Task<FileScanResult>>? Next { get; set; }

        public List<byte[]> Scanned { get; } = [];

        public List<FileScanContext> Contexts { get; } = [];

        public async Task<FileScanResult> ScanAsync(Stream content, FileScanContext context, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            lock (Scanned)
            {
                Scanned.Add(buffer.ToArray());
                Contexts.Add(context);
            }
            return Next is { } next ? await next(cancellationToken) : FileScanResult.Clean;
        }
    }
}

/// <summary>D5 on a booted host: Production without a scanner does not start.</summary>
public class FileScanningStartupTests : FilesTestHost
{
    [Fact]
    public void Production_without_a_scanner_refuses_to_start()
    {
        HostEnvironment = "Production";

        var act = () => Factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*No IFileScanner is registered*");
    }

    [Fact]
    public void Production_with_AllowUnscannedFiles_starts()
    {
        HostEnvironment = "Production";
        ConfigureFiles = files => files.ConfigureOptions(o => o.AllowUnscannedFiles = true);

        using var client = Factory.CreateClient();

        client.Should().NotBeNull();
    }
}
