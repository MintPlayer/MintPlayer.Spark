using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Ingestion;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// The flag list is a caller-chosen document multiplier, so it is bounded.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ The parser writes one <c>FileCoverage</c> per file <b>per flag</b>, on top of the build-level
/// one — so a session naming F flags over N files produces <c>N × (1 + F)</c> documents, and nothing
/// bounds N. On the anonymous fork endpoint every part of that was caller-controlled and free.
/// </para>
/// <para>
/// This was shipped unbounded and found by investigating storage cost rather than by any test —
/// which is the point of pinning it: the multiplier is invisible at the upload site, three files
/// away from where the documents are actually written.
/// </para>
/// </remarks>
public class UploadFlagBoundsTests : CoverageRavenTest
{
    private const long RepoId = 8100;
    private static readonly string Sha = new('a', 40);

    private static UploadIngestRequest Request(string? flags, bool fromFork) => new(
        Repository: new Repository
        {
            GitHubId = RepoId,
            FullName = "acme/widget",
            Name = "widget",
            OwnerLogin = "acme",
        },
        CommitSha: Sha,
        Branch: "feature/x",
        PullRequestNumber: fromFork ? 7 : null,
        BaseRef: null, PrBaseSha: null, ParentSha: null,
        RunId: 1, RunAttempt: 1, Workflow: null, EventName: null, JobName: null,
        Flags: flags,
        RootDir: null, FileList: null, Partial: false, CarryForward: null, BaseSha: null,
        Files: Array.Empty<IFormFile>(),
        ContributedFromFork: fromFork);

    private static async Task<string[]> FlagsOfAsync(IDocumentStore store, string? flags, bool fromFork)
    {
        using var session = store.OpenAsyncSession();
        var ingestor = ActivatorUtilities.CreateInstance<UploadIngestor>(
            new ServiceCollection()
                .AddSingleton(session)
                .AddSingleton<IMessageBus>(new SilentBus())
                .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
                .BuildServiceProvider());

        var result = await ingestor.IngestAsync(Request(flags, fromFork));

        using var verify = store.OpenAsyncSession();
        var build = await verify.LoadAsync<Build>(result.BuildId);
        return build.Sessions.Single(s => s.SessionId == result.SessionId).Flags;
    }

    [Fact]
    public async Task A_fork_upload_cannot_multiply_documents_with_a_long_flag_list()
    {
        using var store = GetDocumentStore();
        var flags = string.Join(',', Enumerable.Range(0, 200).Select(i => $"flag{i}"));

        var stored = await FlagsOfAsync(store, flags, fromFork: true);

        Assert.Equal(4, stored.Length);
    }

    [Fact]
    public async Task A_first_party_upload_is_bounded_too_just_far_more_generously()
    {
        using var store = GetDocumentStore();
        var flags = string.Join(',', Enumerable.Range(0, 200).Select(i => $"flag{i}"));

        var stored = await FlagsOfAsync(store, flags, fromFork: false);

        Assert.Equal(32, stored.Length);
    }

    /// <summary>
    /// The paired negative: an ordinary flag list is untouched. Without this, a cap of zero would
    /// pass the two tests above while silently deleting a shipped feature.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_ordinary_flag_list_passes_through_unchanged(bool fromFork)
    {
        using var store = GetDocumentStore();

        var stored = await FlagsOfAsync(store, "unit,integration", fromFork);

        Assert.Equal(["unit", "integration"], stored);
    }

    /// <summary>
    /// Duplicates compose the same document id, so they were never extra documents — but they are
    /// extra work, and de-duplicating keeps the cap meaningful rather than spendable on repeats.
    /// </summary>
    [Fact]
    public async Task Duplicate_flags_collapse()
    {
        using var store = GetDocumentStore();

        var stored = await FlagsOfAsync(store, "unit,unit,UNIT, unit ", fromFork: false);

        Assert.Equal(["unit"], stored);
    }

    [Fact]
    public async Task No_flags_is_still_no_flags()
    {
        using var store = GetDocumentStore();

        Assert.Empty(await FlagsOfAsync(store, null, fromFork: false));
        Assert.Empty(await FlagsOfAsync(store, "  ,  ,", fromFork: false));
    }

    private sealed class SilentBus : IMessageBus
    {
        public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task BroadcastOnceAsync<TMessage>(TMessage message, string deduplicationKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
