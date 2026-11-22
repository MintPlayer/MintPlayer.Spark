using CodeCoverage.Versioning.Changelog;

namespace CodeCoverage.Versioning.Tests.Changelog;

/// <summary>#693 D5, D6, D9: parsing a commit into an entry, and applying what the forge says about its pull request.</summary>
public class ChangelogEntriesTests
{
    private static ChangelogEntry Parse(string subject, string body = "") => ChangelogEntries.Parse(new ChangeCommit(subject, body));

    private static IReadOnlyDictionary<int, PullRequestFacts> Facts(params PullRequestFacts[] facts) => facts.ToDictionary(f => f.Number);

    [Fact]
    public void Parses_type_scope_bang_issues_and_the_pull_request()
    {
        var entry = Parse("fix(core)!: null id (#251/#252) (#610)");
        (entry.Text, entry.Type, entry.Breaking, entry.PullRequest, entry.Section).Should().Be(("null id", "fix", true, (int?)610, ChangeSection.Breaking));
        entry.Issues.Should().Equal(251, 252);

        var untyped = Parse("[dotnet-11.x] Spark Apps (#690)");
        (untyped.Text, untyped.Type, untyped.PullRequest, untyped.Section).Should().Be(("Spark Apps", (string?)null, (int?)690, ChangeSection.Changes));
    }

    [Fact]
    public void A_breaking_footer_a_backport_trailer_and_dependency_scopes_are_read()
    {
        Parse("feat: x (#1)", "body\n\nBREAKING CHANGE: removed y").Breaking.Should().BeTrue();
        Parse("fix: x (#251) (#610)", "Co-authored-by: a <a@a>\nBackport-of: #609").Key.Should().Be(609);
        Parse("chore(deps): bump a (#5)").Section.Should().Be(ChangeSection.Dependencies);
        Parse("build(deps-dev): bump b (#6)").Section.Should().Be(ChangeSection.Dependencies);
        Parse("docs: guide (#7)").Section.Should().Be(ChangeSection.Dropped);
    }

    [Fact]
    public void An_untyped_old_pull_request_is_typed_from_its_template_box_title_or_bug_issue_and_never_defaults_to_feat()
    {
        var entries = ChangelogEntries.Resolve([Parse("one (#1)"), Parse("two (#2)"), Parse("three (#3)"), Parse("four (#4)")], Facts(
            new PullRequestFacts(1, "one", "- [x] Bug fix\n- [ ] New feature", []),
            new PullRequestFacts(2, "feat!: two", null, []),
            new PullRequestFacts(3, "three", "Closes #9", [], LinkedIssueIsBug: true),
            new PullRequestFacts(4, "four", null, [])));

        entries.Select(e => (e.Type, e.Breaking)).Should().Equal(("fix", false), ("feat", true), ("fix", false), (null, false));
    }

    [Fact]
    public void A_skip_label_drops_and_a_changelog_section_replaces_the_entry()
    {
        var entries = ChangelogEntries.Resolve([Parse("feat: a (#1)"), Parse("feat: b (#2)"), Parse("feat: c (#3)"), Parse("feat: d (#4)")], Facts(
            new PullRequestFacts(1, "feat: a", null, ["Changelog: Skip"]),
            new PullRequestFacts(2, "feat: b", "Intro\n\n## Changelog\n<!-- one bullet per entry -->\n- Adds the picker\n- fix: the picker's focus\n\n## Notes\n- not this", []),
            new PullRequestFacts(3, "feat: c", "## Changelog\nnone", []),
            new PullRequestFacts(4, "feat: d", "## Changelog\n", [])));

        entries.Select(e => (e.Text, e.Type, e.PullRequest)).Should().Equal(("Adds the picker", "feat", (int?)2), ("the picker's focus", "fix", (int?)2));
    }

    [Fact]
    public void An_older_backport_names_its_original_only_in_the_PR_body()
    {
        var entries = ChangelogEntries.Deduplicate(ChangelogEntries.Resolve([Parse("fix: null id (#251) (#609)"), Parse("[dotnet-11.x] fix: null id (#251) (#610)")],
            Facts(new PullRequestFacts(610, "[dotnet-11.x] fix: null id (#251)", "<!-- backport-of: MintPlayer/MintPlayer.Spark#609 -->", [], BackportOf: 609))));

        entries.Should().ContainSingle().Which.PullRequest.Should().Be(609);
    }
}
