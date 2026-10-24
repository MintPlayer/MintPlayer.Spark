using CodeCoverage.PrTitles;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// The title rule (docs/coverage_package_versioning_PRD.md D14): the type's order of sources, the issue suffix, and the
/// fixed point that keeps the app's own edit from changing anything.
/// </summary>
public class PrTitleBuilderTests
{
    private const string Repo = "MintPlayer/MintPlayer.Spark";

    private static Task<PrTitleResult?> Build(string title, string? body = null, string headRef = "topic", bool bot = false,
        params (int Number, string[] Labels)[] issues)
        => PrTitleBuilder.BuildAsync(title, body, headRef, bot, Repo,
            (n, _) => Task.FromResult(issues.Where(i => i.Number == n).Select(i => (IReadOnlyList<string>?)i.Labels).FirstOrDefault()));

    [Fact]
    public async Task A_bare_title_defaults_to_feat()
        => (await Build("Add the release lister"))!.Title.Should().Be("feat: Add the release lister");

    [Fact]
    public async Task The_ticked_template_box_wins_over_the_title()
    {
        var result = await Build("feat: Crash on empty input", "## Type\n- [ ] Feature\n- [x] Bug fix\n");
        result!.Title.Should().Be("fix: Crash on empty input");
        result.TypeSource.Should().Be(EPrTitleTypeSource.Template);
    }

    [Fact]
    public async Task The_title_type_and_its_scope_are_kept()
    {
        var result = await Build("refactor(engine)!: Split the lister");
        result!.Title.Should().Be("refactor(engine)!: Split the lister");
        result.TypeSource.Should().Be(EPrTitleTypeSource.Title);
    }

    [Fact]
    public async Task An_unknown_prefix_is_text_not_a_type()
        => (await Build("WIP: something"))!.Title.Should().Be("feat: WIP: something");

    [Fact]
    public async Task A_bug_labelled_linked_issue_makes_it_a_fix()
    {
        var result = await Build("Crash on empty input", "Closes #12", issues: (12, ["bug"]));
        result!.Title.Should().Be("fix: Crash on empty input (#12)");
        result.TypeSource.Should().Be(EPrTitleTypeSource.LinkedIssue);
    }

    [Fact]
    public async Task A_bug_labelled_branch_issue_makes_it_a_fix_and_joins_the_suffix()
    {
        var result = await Build("Crash on empty input", "Fixes #12", "fix/issue-40-crash", issues: [(12, ["enhancement"]), (40, ["Bug"])]);
        result!.Title.Should().Be("fix: Crash on empty input (#12/#40)");
        result.TypeSource.Should().Be(EPrTitleTypeSource.BranchIssue);
    }

    [Fact]
    public async Task A_branch_number_that_is_no_issue_is_left_out()
        => (await Build("Something", headRef: "feat/issue153-package-versioning"))!.Title.Should().Be("feat: Something");

    [Fact]
    public async Task Another_repositorys_issue_is_ignored()
    {
        var result = await Build("Something", "Closes other/repo#12, resolves MintPlayer/MintPlayer.Spark#13", issues: [(12, ["bug"]), (13, [])]);
        result!.Title.Should().Be("feat: Something (#13)");
    }

    [Fact]
    public async Task A_bots_own_type_is_kept_over_the_template()
    {
        var result = await Build("chore(deps): bump x from 1 to 2", "- [x] Feature", bot: true);
        result!.Title.Should().Be("chore(deps): bump x from 1 to 2");
    }

    [Fact]
    public async Task A_stale_suffix_is_replaced_not_stacked()
        => (await Build("feat: Something (#1) (#2/#3)", "Closes #4", issues: (4, [])))!.Title.Should().Be("feat: Something (#4)");

    [Fact]
    public async Task A_type_from_elsewhere_drops_the_old_scope()
        => (await Build("feat(ui): Crash", "Closes #5\n- [x] Bug fix", issues: (5, [])))!.Title.Should().Be("fix: Crash (#5)");

    [Fact]
    public async Task An_empty_title_builds_nothing()
        => (await Build("  (#3) ")).Should().BeNull();

    [Theory]
    [InlineData("Crash on empty input", "Closes #12", "fix/issue-40")]
    [InlineData("feat(ui): Add it (#9)", "- [x] Documentation", "topic")]
    [InlineData("chore: x", null, "issues/#40")]
    public async Task Building_from_the_built_title_changes_nothing(string title, string? body, string headRef)
    {
        (int, string[])[] issues = [(12, ["bug"]), (40, [])];
        var first = await Build(title, body, headRef, issues: issues);
        var second = await Build(first!.Title, body, headRef, issues: issues);
        second!.Title.Should().Be(first.Title);
    }

    [Fact]
    public async Task Each_issue_is_looked_up_once()
    {
        var lookups = 0;
        await PrTitleBuilder.BuildAsync("x", "Closes #7", "issue-7", false, Repo,
            (_, _) => { lookups++; return Task.FromResult<IReadOnlyList<string>?>([]); });
        lookups.Should().Be(1);
    }
}
