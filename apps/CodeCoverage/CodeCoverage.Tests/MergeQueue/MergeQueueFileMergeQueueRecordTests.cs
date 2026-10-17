using CodeCoverage.Entities;
using CodeCoverage.LookupReferences;
using CodeCoverage.MergeQueue;
using Xunit;
using static CodeCoverage.Tests.MergeQueue.MergeQueueHarness;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// D-O16 (live test 2026-10-17): <c>Repository.MergeQueueInCoverageYml</c> was empty although the repository's coverage.yml
/// said <c>mergeQueue: enabled: true</c>, because only a dependency run recorded it. The merge queue's own read of the
/// default branch's coverage.yml records it too, as a field patch on the dependency run document.
/// </summary>
public class MergeQueueFileMergeQueueRecordTests : CoverageRavenTest
{
    [Fact]
    public async Task The_merge_queues_read_of_coverage_yml_records_its_merge_queue_on_the_dependency_run()
    {
        var h = new MergeQueueHarness(GetDocumentStore(), mergeBase: BaseSha);
        await h.SeedAsync();
        using (var session = h.Store.OpenAsyncSession())
        {
            await session.StoreAsync(new DependencyUpdateRun { Repository = RepositoryId }, DependencyUpdateRun.ForRepository(RepositoryId));
            await session.SaveChangesAsync();
        }

        await h.EnqueueAsync();

        using (var session = h.Store.OpenAsyncSession())
            (await session.LoadAsync<DependencyUpdateRun>(DependencyUpdateRun.ForRepository(RepositoryId))).FileMergeQueue.Should().Be("enabled");
    }

    [Fact]
    public async Task A_repository_without_a_dependency_run_records_nothing_and_the_queue_still_works()
    {
        var h = new MergeQueueHarness(GetDocumentStore(), mergeBase: BaseSha);
        await h.SeedAsync();

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Merged);
        using var session = h.Store.OpenAsyncSession();
        (await session.LoadAsync<DependencyUpdateRun>(DependencyUpdateRun.ForRepository(RepositoryId))).Should().BeNull();
    }
}
