using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.PrSize;
using MintPlayer.Assertions;
using Xunit;

namespace CodeCoverage.Tests.PrSize;

/// <summary>The label-definition reconcile (docs/coverage_pr_size_labels_PRD.md D3), against GitHub's semantics from S1.</summary>
public class PrSizeLabelReconcilerTests
{
    private static (FakeForgeLabels Forge, PrSizeLabelReconciler Reconciler, Repository Repository) Setup()
    {
        var forge = new FakeForgeLabels();
        return (forge, new PrSizeLabelReconciler(forge, PrSizeLabelPacing.None), new Repository { FullName = "acme/widgets" });
    }

    private static PrSizeScale Scale(Action<List<PrSizeLabel>>? change = null)
    {
        var rows = PrSizeDefaults.Scale();
        change?.Invoke(rows);
        return new PrSizeScale(rows);
    }

    [Fact]
    public async Task A_first_reconcile_creates_every_row_and_records_it()
    {
        var (forge, reconciler, repository) = Setup();

        await reconciler.ReconcileAsync(repository, Scale(), default);

        forge.Labels.Select(l => l.Name).Should().BeEquivalentTo(["size/XS", "size/S", "size/M", "size/L", "size/XL", "size/XXL"]);
        forge.Find("size/M")!.Color.Should().Be("eebb00");
        forge.Find("size/M")!.Description.Should().Be("Denotes a PR that changes 30-99 lines");
        repository.PrSizeLabelState.Select(s => s.RowId).Should().BeEquivalentTo(["xs", "s", "m", "l", "xl", "xxl"]);
        PrSizeLabelReconciler.IsCurrent(repository, Scale()).Should().BeTrue();
    }

    [Fact]
    public async Task A_second_reconcile_writes_nothing()
    {
        var (forge, reconciler, repository) = Setup();
        await reconciler.ReconcileAsync(repository, Scale(), default);
        forge.Writes.Clear();

        await reconciler.ReconcileAsync(repository, Scale(), default);

        forge.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_label_the_user_made_with_the_rows_name_is_adopted_and_restyled()
    {
        var (forge, reconciler, repository) = Setup();
        forge.Labels.Add(new ForgeLabel("Size/m", "123456", "mine", false));

        await reconciler.ReconcileAsync(repository, Scale(), default);

        forge.Writes.Should().Contain("update Size/m -> size/M");
        forge.Writes.Should().NotContain("create size/M");
        forge.Labels.Count(l => string.Equals(l.Name, "size/M", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        forge.Find("size/M")!.Name.Should().Be("size/M");
    }

    [Fact]
    public async Task A_renamed_row_patches_its_label_so_pull_requests_keep_it()
    {
        var (forge, reconciler, repository) = Setup();
        await reconciler.ReconcileAsync(repository, Scale(), default);
        forge.PullRequests[7] = ["size/M"];
        forge.Writes.Clear();

        await reconciler.ReconcileAsync(repository, Scale(rows => rows[2].Name = "size/medium"), default);

        forge.Writes.Should().Equal("update size/M -> size/medium");
        forge.PullRequests[7].Should().Equal("size/medium");
        repository.PrSizeLabelState.Single(s => s.RowId == "m").Name.Should().Be("size/medium");
    }

    [Fact]
    public async Task A_renamed_row_whose_label_was_deleted_by_hand_is_created()
    {
        var (forge, reconciler, repository) = Setup();
        await reconciler.ReconcileAsync(repository, Scale(), default);
        forge.Labels.Remove(forge.Find("size/M")!);

        await reconciler.ReconcileAsync(repository, Scale(rows => rows[2].Name = "size/medium"), default);

        forge.Find("size/medium").Should().NotBeNull();
    }

    [Fact]
    public async Task A_removed_row_deletes_its_label_and_forgets_it()
    {
        var (forge, reconciler, repository) = Setup();
        await reconciler.ReconcileAsync(repository, Scale(), default);
        forge.Writes.Clear();

        await reconciler.ReconcileAsync(repository, Scale(rows => rows.RemoveAt(5)), default);

        forge.Writes.Should().Contain("delete size/XXL");
        forge.Find("size/XXL").Should().BeNull();
        repository.PrSizeLabelState.Should().NotContain(s => s.RowId == "xxl");
    }

    [Fact]
    public async Task Labels_the_app_never_wrote_are_never_touched()
    {
        var (forge, reconciler, repository) = Setup();
        forge.Labels.Add(new ForgeLabel("size/foo", "000000", null, false));

        await reconciler.ReconcileAsync(repository, Scale(), default);
        await reconciler.ReconcileAsync(repository, Scale(rows => rows.RemoveAt(0)), default);
        await reconciler.RemoveAsync(repository, default);

        forge.Writes.Should().NotContain(w => w.Contains("size/foo"));
        forge.Find("size/foo").Should().NotBeNull();
    }

    [Fact]
    public async Task A_failure_halfway_leaves_the_state_true_and_the_retry_converges()
    {
        var (forge, reconciler, repository) = Setup();
        forge.FailOn["create size/L"] = new ForgeRequestException("boom", 500, null);

        var first = () => reconciler.ReconcileAsync(repository, Scale(), default);
        await first.Should().ThrowAsync<ForgeRequestException>();
        repository.PrSizeLabelState.Select(s => s.RowId).Should().BeEquivalentTo(["xs", "s", "m"]);

        await reconciler.ReconcileAsync(repository, Scale(), default);

        forge.Labels.Should().HaveCount(6);
        PrSizeLabelReconciler.IsCurrent(repository, Scale()).Should().BeTrue();
    }

    [Fact]
    public async Task An_archived_label_is_reported_and_left_archived()
    {
        var (forge, reconciler, repository) = Setup();
        forge.Labels.Add(new ForgeLabel("size/L", "ee9900", "Denotes a PR that changes 100-499 lines", true));

        var archived = await reconciler.ReconcileAsync(repository, Scale(), default);

        archived.Should().BeEquivalentTo(["size/L"]);
        forge.Find("size/L")!.Archived.Should().BeTrue();
    }

    [Fact]
    public async Task Remove_deletes_every_recorded_label_and_clears_the_state()
    {
        var (forge, reconciler, repository) = Setup();
        await reconciler.ReconcileAsync(repository, Scale(), default);

        await reconciler.RemoveAsync(repository, default);

        forge.Labels.Should().BeEmpty();
        repository.PrSizeLabelState.Should().BeEmpty();
    }
}
