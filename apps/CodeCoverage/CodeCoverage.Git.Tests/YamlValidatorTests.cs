using System.Text;
using CodeCoverage.Git.MergeQueue;
using CodeCoverage.Git.MergeQueue.Resolution;

namespace CodeCoverage.Git.Tests;

/// <summary>
/// <c>yaml</c> (docs/code-coverage/dependency-updates-PRD.md R15): parses, and refuses what makes a document's meaning
/// depend on the parser or blow up when expanded.
/// </summary>
public class YamlValidatorTests
{
    private static readonly YamlValidator Yaml = new();

    [Theory]
    [InlineData("a: 1\nb: [1, 2]\nc: {d: e}\n")]
    [InlineData("---\na: 1\n---\nb: 2\n")]
    [InlineData("run: |\n  echo\t\"tabs inside a block scalar are content\"\n")]
    [InlineData("base: &b\n  x: 1\nother: *b\n")]
    [InlineData("\uFEFFa: 1\n")]
    [InlineData("")]
    public void Well_formed_yaml_is_valid(string text)
    {
        var verdict = Yaml.Validate(text);

        verdict.Problem.Should().BeNull();
        verdict.Valid.Should().BeTrue();
    }

    [Theory]
    [InlineData("a: 1\na: 2\n", "duplicate key")]
    [InlineData("jobs:\n  b:\n    x: 1\n    'x': 2\n", "duplicate key")]
    [InlineData("? [a, b]\n: 1\n", "not a scalar")]
    // YamlDotNet's scanner refuses a tab-indented block entry itself (at the mapping's start, 1:1), before the
    // validator's own tab check runs; the flow-continuation case below is the one only that check catches.
    [InlineData("a:\n\t- b\n", "not well-formed")]
    [InlineData("a: [1, 2\n", "not well-formed")]
    [InlineData("a: *missing\n", "an alias to an anchor that is not defined")]
    public void Ambiguous_or_broken_yaml_is_invalid(string text, string problem)
    {
        var verdict = Yaml.Validate(text);

        verdict.Valid.Should().BeFalse();
        verdict.Problem!.Should().Contain(problem);
    }

    [Fact]
    public void A_tab_in_the_indentation_of_a_flow_continuation_is_invalid()
    {
        var verdict = Yaml.Validate("a: [1,\n\t2]\n");

        verdict.Valid.Should().BeFalse();
        verdict.Problem.Should().Be("a tab in a line's indentation");
        verdict.Line.Should().Be(2L);
    }

    [Fact]
    public void An_alias_bomb_is_invalid()
    {
        // The "billion laughs" shape: each level holds ten aliases of the previous one.
        var text = new StringBuilder("l0: &l0 [x, x, x, x, x, x, x, x, x, x]\n");
        for (var i = 1; i <= 6; i++)
            text.Append($"l{i}: &l{i} [{string.Join(", ", Enumerable.Repeat($"*l{i - 1}", 10))}]\n");

        var verdict = Yaml.Validate(text.ToString());

        verdict.Valid.Should().BeFalse();
        verdict.Problem!.Should().Contain("aliases");
    }

    [Fact]
    public void An_expansion_over_the_node_cap_is_invalid_even_with_few_aliases()
    {
        var text = new StringBuilder("l0: &l0 [");
        text.Append(string.Join(", ", Enumerable.Repeat("x", 1000)));
        text.Append("]\n");
        for (var i = 1; i <= 5; i++)
            text.Append($"l{i}: &l{i} [*l{i - 1}, *l{i - 1}, *l{i - 1}, *l{i - 1}, *l{i - 1}, *l{i - 1}, *l{i - 1}, *l{i - 1}]\n");

        var verdict = Yaml.Validate(text.ToString());

        verdict.Valid.Should().BeFalse();
        verdict.Problem!.Should().Contain($"more than {YamlDocument.MaxExpandedNodes} nodes");
    }

    [Fact]
    public void The_validators_are_named_on_the_wire()
    {
        MergeQueueWireRules.Validators.Should().Contain(MergeQueueWireRules.Yaml);
        MergeQueueWireRules.Validators.Should().Contain(MergeQueueWireRules.GitHubWorkflow);
        ConflictResolverRegistry.Default.Validator(MergeQueueWireRules.Yaml).Should().BeOfType<YamlValidator>();
        ConflictResolverRegistry.Default.Validator(MergeQueueWireRules.GitHubWorkflow).Should().BeOfType<GitHubWorkflowValidator>();
    }
}
