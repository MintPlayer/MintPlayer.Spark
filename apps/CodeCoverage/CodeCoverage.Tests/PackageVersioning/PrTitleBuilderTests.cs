using CodeCoverage.Versioning.Changelog;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// The title rule (docs/coverage_package_versioning_PRD.md D14, as #693 D6 and D12 changed it): the type's order of explicit
/// sources, no default and no branch name, the issue suffix, and the fixed point that keeps the app's own edit from
/// changing anything.
/// </summary>
public class PrTitleBuilderTests
{
    private const string Repo = "MintPlayer/MintPlayer.Spark";

    private static Task<PrTitleResult?> Build(string title, string? body = null, bool bot = false, string? chosen = null,
        params (int Number, string[] Labels)[] issues)
        => PrTitleBuilder.BuildAsync(title, body, bot, Repo,
            (n, _) => Task.FromResult(issues.Where(i => i.Number == n).Select(i => (IReadOnlyList<string>?)i.Labels).FirstOrDefault()), chosen);

    // #693 D12: no silent feat.
    [Fact]
    public async Task A_bare_title_stays_untyped()
    {
        var result = await Build("Add the release lister");
        (result!.Title, result.Type, result.TypeSource).Should().Be(("Add the release lister", (string?)null, EPrTitleTypeSource.None));
    }

    [Fact]
    public async Task The_ticked_template_box_wins_over_the_title()
    {
        var result = await Build("feat: Crash on empty input", "## Type\n- [ ] Feature\n- [x] Bug fix\n");
        result!.Title.Should().Be("fix: Crash on empty input");
        result.TypeSource.Should().Be(EPrTitleTypeSource.Template);
    }

    [Fact]
    public async Task A_chosen_type_wins_over_everything_but_a_bots_own()
    {
        var result = await Build("Crash on empty input", "- [x] Feature", chosen: "fix");
        (result!.Title, result.TypeSource).Should().Be(("fix: Crash on empty input", EPrTitleTypeSource.Button));

        (await Build("chore(deps): bump x", bot: true, chosen: "feat"))!.Title.Should().Be("chore(deps): bump x");
        (await Build("Something", chosen: "nonsense"))!.Type.Should().BeNull("only a known type is taken");
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
        => (await Build("WIP: something"))!.Type.Should().BeNull();

    [Fact]
    public async Task A_bug_labelled_linked_issue_makes_it_a_fix()
    {
        var result = await Build("Crash on empty input", "Closes #12", issues: (12, ["bug"]));
        result!.Title.Should().Be("fix: Crash on empty input (#12)");
        result.TypeSource.Should().Be(EPrTitleTypeSource.LinkedIssue);
    }

    [Fact]
    public async Task Another_repositorys_issue_is_ignored()
    {
        var result = await Build("feat: Something", "Closes other/repo#12, resolves MintPlayer/MintPlayer.Spark#13", issues: [(12, ["bug"]), (13, [])]);
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
    [InlineData("Crash on empty input", "Closes #12")]
    [InlineData("feat(ui): Add it (#9)", "- [x] Documentation")]
    [InlineData("chore: x", null)]
    [InlineData("Untyped (#4)", "Closes #40")]
    public async Task Building_from_the_built_title_changes_nothing(string title, string? body)
    {
        (int, string[])[] issues = [(12, ["bug"]), (40, [])];
        var first = await Build(title, body, issues: issues);
        var second = await Build(first!.Title, body, issues: issues);
        second!.Title.Should().Be(first.Title);
    }

    [Fact]
    public async Task Each_issue_is_looked_up_once()
    {
        var lookups = 0;
        await PrTitleBuilder.BuildAsync("x", "Closes #7\nFixes #7", false, Repo,
            (_, _) => { lookups++; return Task.FromResult<IReadOnlyList<string>?>([]); });
        lookups.Should().Be(1);
    }
}
