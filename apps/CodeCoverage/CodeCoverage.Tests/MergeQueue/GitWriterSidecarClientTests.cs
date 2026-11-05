using System.Net;
using CodeCoverage.CommitGraph;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>The app's side of the git writer's wire (merge-queue PRD §0 D65): the queue-branch push and the publish carry
/// their prepare's fields at the top level, as the writer's request types extend the prepare's.</summary>
public class GitWriterSidecarClientTests
{
    private static readonly GitWriterPrepareRequest Prepare = new(
        "github", "910040", "https://github.com/acme/widget.git", new SidecarCredential("x-access-token", "job-token"), "master",
        "1111111111111111111111111111111111111111", "refs/pull/7/head", "3333333333333333333333333333333333333333",
        [new GitWriterAutoResolve("**/App_Data/modelHashes.json", AnyLine: true)],
        new GitWriterIdentity("author", "author@users.noreply.github.com", new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.Zero)),
        new GitWriterIdentity("CodeCoverage merge queue", "merge-queue@noreply.coverage.mintplayer.com", new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.Zero)),
        "Add widgets (#7)");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_queue_branch_push_is_one_flat_object_with_the_prepares_fields(bool headFromFork)
    {
        var body = GitWriterSidecarClient.Body(new GitWriterQueueBranchRequest(Prepare, "4444444444444444444444444444444444444444", "mq/master/pr-7", null, headFromFork));

        body.ContainsKey("prepare").Should().BeFalse();
        body["baseSha"]!.GetValue<string>().Should().Be("1111111111111111111111111111111111111111");
        body["headSha"]!.GetValue<string>().Should().Be("3333333333333333333333333333333333333333");
        body["author"]!["date"]!.GetValue<DateTimeOffset>().Should().Be(new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.Zero));
        body["committer"]!["date"].Should().NotBeNull();
        body["credential"]!["password"]!.GetValue<string>().Should().Be("job-token");
        body["preparedSha"]!.GetValue<string>().Should().Be("4444444444444444444444444444444444444444");
        body["queueBranch"]!.GetValue<string>().Should().Be("mq/master/pr-7");
        body.ContainsKey("expectedQueueSha").Should().BeTrue("null means the branch must not exist, so it is sent");
        // O25, §0 D67: the writer requires it, and refuses a fork's .github change itself; nothing on the wire allows one.
        body["headFromFork"]!.GetValue<bool>().Should().Be(headFromFork);
        body.ContainsKey("allowWorkflowChanges").Should().BeFalse();
    }

    [Fact]
    public void A_publish_is_one_flat_object_with_the_prepares_fields()
    {
        var body = GitWriterSidecarClient.Body(new GitWriterPublishRequest(Prepare, "4444444444444444444444444444444444444444", null, "feature-7", null, []));

        body.ContainsKey("prepare").Should().BeFalse();
        body["headSha"]!.GetValue<string>().Should().Be("3333333333333333333333333333333333333333", "the publish's lease");
        body["message"]!.GetValue<string>().Should().Be("Add widgets (#7)");
        body["prBranch"]!.GetValue<string>().Should().Be("feature-7");
        body["preparedSha"]!.GetValue<string>().Should().Be("4444444444444444444444444444444444444444");
    }

    // ── The wire: routes, refusals, failures ─────────────────────────────────────────────────────────

    private static readonly SidecarCredential Credential = new("x-access-token", "job-token");

    private static readonly GitWriterDependencyRequest Dependency = new("github", "910040", "https://github.com/acme/widget.git", Credential, "master", null);

    /// <summary>Answers every request with <paramref name="status"/> and <paramref name="body"/>, and records it.</summary>
    private sealed class Writer(HttpStatusCode status = HttpStatusCode.OK, string? body = "{}") : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            var response = new HttpResponseMessage(status);
            if (body is not null) response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    private static GitWriterSidecarClient Client(Writer writer, string? baseUrl = "http://coverage-git-writer.test/")
        => new(new HttpClient(writer) { BaseAddress = baseUrl is null ? null : new Uri(baseUrl) }, Options.Create(new MergeQueueOptions()));

    public static TheoryData<string, Func<IGitWriterSidecar, Task>> Routes => new()
    {
        { "POST /jobs/prepare", w => w.PrepareAsync(Prepare) },
        { "POST /jobs/queue-branch", w => w.PushQueueBranchAsync(new GitWriterQueueBranchRequest(Prepare, "4444444444444444444444444444444444444444", "mq/master/pr-7", null, false)) },
        { "POST /jobs/publish", w => w.PublishAsync(new GitWriterPublishRequest(Prepare, "4444444444444444444444444444444444444444", null, "feature-7", null, [])) },
        { "DELETE /jobs/queue-branch", w => w.DeleteQueueBranchAsync(new GitWriterDeleteQueueBranchRequest("github", "910040", "https://github.com/acme/widget.git", Credential, "mq/master/pr-7", null)) },
        { "POST /jobs/dependencies/work-ref", w => w.DependencyWorkRefAsync(Dependency) },
        { "POST /jobs/dependencies/validate", w => w.DependencyValidateAsync(Dependency) },
        { "POST /jobs/dependencies/rolling", w => w.DependencyRollingAsync(Dependency) },
        { "POST /jobs/dependencies/files", w => w.DependencyFilesAsync(Dependency) },
        { "DELETE /jobs/dependencies/work-ref", w => w.DependencyDeleteWorkRefAsync(Dependency) },
        { "POST /jobs/versioning/onboarding", w => w.VersioningOnboardingAsync(Dependency) },
        { "POST /jobs/tags", w => w.TagsAsync(new GitWriterTagsRequest("github", "910040", "https://github.com/acme/widget.git", Credential, ["master"], null!, [])) },
        { "POST /jobs/versioning/run", w => w.VersioningRunAsync(new GitWriterVersioningRequest("github", "910040", "https://github.com/acme/widget.git", Credential, "master", "master", null, null!, null!, null!)) },
        { "POST /jobs/versioning/backport", w => w.VersioningBackportAsync(new GitWriterBackportRequest("github", "910040", "https://github.com/acme/widget.git", Credential, "master", "abc", 7, "Fix", [], null!, [])) },
        { "POST /jobs/versioning/plan", w => w.VersioningPlanAsync(new GitWriterVersioningPlanRequest("github", "910040", "https://github.com/acme/widget.git", Credential, "master", "master", "abc", null, null!)) },
    };

    /// <summary>Every job but the four the merge queue requires of any writer.</summary>
    public static TheoryData<string, Func<IGitWriterSidecar, Task>> OptionalRoutes
    {
        get
        {
            var optional = new TheoryData<string, Func<IGitWriterSidecar, Task>>();
            foreach (var row in Routes.Skip(4)) optional.Add((string)row[0], (Func<IGitWriterSidecar, Task>)row[1]);
            return optional;
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Every_job_goes_to_its_own_route(string route, Func<IGitWriterSidecar, Task> job)
    {
        var writer = new Writer();

        await job(Client(writer));

        writer.Requests.Should().Equal(route);
    }

    /// <summary>A writer built before a job existed: the interface's default refuses it, it is never a silent no-op.</summary>
    [Theory]
    [MemberData(nameof(OptionalRoutes))]
    public async Task A_writer_without_a_job_refuses_it(string route, Func<IGitWriterSidecar, Task> job)
    {
        _ = route;
        await ((Func<Task>)(() => job(new FourJobWriter()))).Should().ThrowAsync<NotSupportedException>();
    }

    private sealed class FourJobWriter : IGitWriterSidecar
    {
        public bool IsConfigured => true;
        public Task<GitWriterPrepareResult> PrepareAsync(GitWriterPrepareRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<GitWriterQueueBranchResult> PushQueueBranchAsync(GitWriterQueueBranchRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<GitWriterPublishResult> PublishAsync(GitWriterPublishRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<GitWriterDeleteResult> DeleteQueueBranchAsync(GitWriterDeleteQueueBranchRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    [Fact]
    public async Task An_unconfigured_writer_sends_nothing()
    {
        var writer = new Writer();
        var client = Client(writer, baseUrl: null);

        client.IsConfigured.Should().BeFalse();
        await ((Func<Task>)(() => client.PrepareAsync(Prepare))).Should().ThrowAsync<InvalidOperationException>().WithMessage("*GitWriterUrl*");
        writer.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"outcome":"refused","field":"prBranch","detail":"a branch name"}""", "prBranch", "a branch name")]
    [InlineData(null, "request", "valid (the writer gave no reason)")]
    [InlineData("<html>Bad Request</html>", "request", "valid (the writer gave no reason)")]
    public async Task A_400_is_a_refusal_naming_the_field_and_its_rule(string? body, string field, string rule)
    {
        var client = Client(new Writer(HttpStatusCode.BadRequest, body));

        var refusal = (await ((Func<Task>)(() => client.PublishAsync(new GitWriterPublishRequest(Prepare, "4444444444444444444444444444444444444444", null, "feature-7", null, []))))
            .Should().ThrowAsync<GitWriterRefusedException>()).Which;

        (refusal.Job, refusal.Field, refusal.Rule).Should().Be(("publish", field, rule));
        refusal.Message.Should().Be($"The git writer refused the publish request: {field} must be {rule}.");
    }

    [Fact]
    public async Task Any_other_failure_status_is_a_transport_failure_and_an_empty_body_an_error()
    {
        await ((Func<Task>)(() => Client(new Writer(HttpStatusCode.BadGateway)).PrepareAsync(Prepare))).Should().ThrowAsync<HttpRequestException>();
        await ((Func<Task>)(() => Client(new Writer(body: "null")).PrepareAsync(Prepare))).Should().ThrowAsync<InvalidOperationException>().WithMessage("*jobs/prepare*empty body*");
    }

    [Fact]
    public void Spark_generated_files_are_take_base_on_any_line()
        => GitWriterAutoResolve.SparkGeneratedFiles.Should().OnlyContain(rule => rule.AnyLine && rule.Strategy == null && rule.Keys == null);

    [Fact]
    public void A_prepare_is_sent_as_it_is()
    {
        var body = GitWriterSidecarClient.Body(Prepare);

        body["baseBranch"]!.GetValue<string>().Should().Be("master");
        body.ContainsKey("queueBranch").Should().BeFalse("§0 D65: the prepare pushes nothing");
    }
}
