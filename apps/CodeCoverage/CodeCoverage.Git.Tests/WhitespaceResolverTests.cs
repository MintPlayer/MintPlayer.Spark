using CodeCoverage.Git.MergeQueue;
using CodeCoverage.Git.MergeQueue.Resolution;
using CodeCoverage.Git.Tests._Infrastructure;

namespace CodeCoverage.Git.Tests;

/// <summary>
/// <c>whitespace</c> (#657, docs/coverage_file_formatter_PRD.md D10, D17) on real repositories: a conflict whose regions
/// differ only in whitespace takes the pull request's side and always regenerates; anything else evicts. <c>main</c> is the
/// base branch, <c>pr</c> the pull request.
/// </summary>
public class WhitespaceResolverTests
{
    private const string Path = "src/Widget.cs";

    private static readonly AutoResolveRule Rule = AutoResolveRule.Create("**/*.cs", MergeQueueWireRules.Whitespace);

    private static string Fixture(TestRepo repo, string @base, string main, string pr)
    {
        repo.Write(Path, @base);
        repo.Commit("base");
        repo.InWork("branch", "pr");
        repo.Write(Path, main);
        repo.Commit("A");
        repo.Checkout("pr");
        repo.Write(Path, pr);
        repo.Commit("B");
        return repo.Bare();
    }

    private static Task<MergeOutcome> MergeAsync(string bare)
        => new MergeQueueResolver(TestRepo.Runner()).MergeAsync(bare, "main", "pr", [Rule], CancellationToken.None);

    private static string Blob(string bare, string tree) => TestRepo.Git(bare, "cat-file", "blob", $"{tree}:{Path}");

    [Fact]
    public async Task D17_a_whitespace_only_conflict_takes_the_pull_request_s_side_and_regenerates()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo,
            "class Widget\n{\n    int a;\n    int b;\n}\n",
            "class Widget\n{\n    int  a;\n    int b;\n}\n",         // the base branch reflowed a line
            "class Widget\n{\n  int a;\n    int b;\n}\n");           // the pull request indented it differently

        var resolved = (await MergeAsync(bare)).Should().BeOfType<MergeOutcome.Resolved>().Which;

        Blob(bare, resolved.Tree).Should().Be("class Widget\n{\n  int a;\n    int b;\n}\n");
        resolved.Resolutions.Should().Equal([new ResolvedPath(Path, MergeQueueWireRules.Whitespace, true, 0)]);
    }

    [Fact]
    public async Task D17_the_pull_request_s_other_changes_are_kept()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo,
            "class Widget\n{\n    int a;\n\n\n\n    int b;\n}\n",
            "class Widget\n{\n    int a ;\n\n\n\n    int b;\n}\n",
            "class Widget\n{\n    int  a;\n\n\n\n    int c;\n}\n");

        var resolved = (await MergeAsync(bare)).Should().BeOfType<MergeOutcome.Resolved>().Which;

        Blob(bare, resolved.Tree).Should().Be("class Widget\n{\n    int  a;\n\n\n\n    int c;\n}\n");
    }

    [Fact]
    public async Task D10_a_conflict_that_changes_more_than_whitespace_evicts()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo,
            "class Widget\n{\n    int a;\n}\n",
            "class Widget\n{\n    int  a;\n}\n",
            "class Widget\n{\n    long a;\n}\n");

        var evicted = (await MergeAsync(bare)).Should().BeOfType<MergeOutcome.Evicted>().Which;

        evicted.Reason.Should().Be(EvictionReason.ResolverRefused);
        evicted.Detail.Should().Contain("more than whitespace");
    }

    [Fact]
    public async Task D10_an_add_add_conflict_evicts()
    {
        using var repo = new TestRepo();
        repo.Write("README.md", "x\n");
        repo.Commit("base");
        repo.InWork("branch", "pr");
        repo.Write(Path, "class A {}\n");
        repo.Commit("A");
        repo.Checkout("pr");
        repo.Write(Path, "class  A {}\n");
        repo.Commit("B");

        (await MergeAsync(repo.Bare())).Should().BeOfType<MergeOutcome.Evicted>();
    }

    [Theory]
    [InlineData(new[] { "a b\n" }, new[] { "ab\n" }, true)]
    [InlineData(new[] { "a\n", "b\n" }, new[] { "a b\n" }, true)]     // a joined line is the same text
    [InlineData(new[] { "\"a b\"\n" }, new[] { "\"ab\"\n" }, true)]  // a string literal: the pre-filter passes it, the runner's proof refuses it
    [InlineData(new[] { "a\n" }, new[] { "b\n" }, false)]
    public void D17_the_pre_filter_compares_without_whitespace(string[] ours, string[] theirs, bool equal)
        => WhitespaceResolver.EqualWithoutWhitespace(ours, theirs).Should().Be(equal);

    [Fact]
    public void D17_a_rule_that_does_not_regenerate_is_refused_on_the_wire()
    {
        AutoResolveRule.FromWire("**/*.cs", null, false, MergeQueueWireRules.Whitespace, null, regenerate: false).Should().BeNull();
        AutoResolveRule.FromWire("**/*.cs", null, false, MergeQueueWireRules.Whitespace, null, regenerate: null).Should().NotBeNull();
    }

    [Fact]
    public void D10_the_formatting_preset_is_whitespace_with_formatting_yml_and_leaves_whitespace_significant_types_alone()
    {
        var preset = AutoResolvePresets.Presets["formatting"];

        preset.Should().AllSatisfy(r => (r.Strategy, r.Regenerate, r.Action).Should().Be((MergeQueueWireRules.Whitespace, (bool?)true, "formatting.yml")));
        preset.Select(r => r.Path).Should().Contain("**/*.cs").And.Contain("**/*.ts")
            .And.NotContain("**/*.yml").And.NotContain("**/*.py").And.NotContain("**/*.md").And.NotContain("**/*.json");
    }
}
