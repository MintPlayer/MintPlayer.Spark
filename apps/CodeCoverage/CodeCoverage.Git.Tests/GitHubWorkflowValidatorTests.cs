using CodeCoverage.Git.MergeQueue.Resolution;

namespace CodeCoverage.Git.Tests;

/// <summary>
/// <c>github-workflow</c> and its before/after comparison (docs/code-coverage/dependency-updates-PRD.md R15, gap C,
/// §13.11.3 #7–#9), with the target <c>action.yml</c> checks on the REAL pairs captured in spike S12
/// (Fixtures/ActionYml, SOURCE.md: checkout v4/v5, upload-artifact v3/v4, setup-node v4/v5), the fixtures the runner's
/// interface-diff tests share.
/// </summary>
public class GitHubWorkflowValidatorTests
{
    private static readonly GitHubWorkflowValidator Validator = new();

    private static string ActionYml(string repo, string tag) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ActionYml", repo, tag, "action.yml"));

    private const string Workflow =
        "name: ci\n" +
        "on: [pull_request]\n" +
        "jobs:\n" +
        "  build:\n" +
        "    runs-on: ubuntu-latest\n" +
        "    steps:\n" +
        "      - uses: actions/checkout@v4\n" +
        "        with:\n" +
        "          fetch-depth: 0\n" +
        "      - uses: actions/setup-node@v4\n" +
        "        with:\n" +
        "          node-version: 20\n" +
        "      - run: npm ci\n" +
        "      - id: up\n" +
        "        uses: actions/upload-artifact@v3\n" +
        "        with:\n" +
        "          path: dist\n" +
        "      - run: echo ${{ steps.up.outputs.artifact-id }}\n";

    private static WorkflowChangeRules Rules(params (string Old, string New)[] uses)
        => new(uses.ToHashSet(), new HashSet<(string, string, string)>());

    [Fact]
    public void A_workflow_and_a_composite_action_are_valid()
    {
        Validator.Validate(Workflow).Valid.Should().BeTrue();
        Validator.Validate("name: x\nruns:\n  using: composite\n  steps:\n    - uses: actions/setup-dotnet@v4\n    - run: dotnet build\n      shell: bash\n").Valid.Should().BeTrue();
        Validator.Validate("on: push\njobs:\n  call:\n    uses: octo/repo/.github/workflows/build.yml@v1\n").Valid.Should().BeTrue();
    }

    [Theory]
    [InlineData("actions_checkout", "v4")]
    [InlineData("actions_checkout", "v5")]
    [InlineData("actions_upload-artifact", "v3")]
    [InlineData("actions_upload-artifact", "v4")]
    [InlineData("actions_setup-node", "v4")]
    [InlineData("actions_setup-node", "v5")]
    public void Every_real_action_yml_is_a_valid_action(string repo, string tag)
    {
        var verdict = Validator.Validate(ActionYml(repo, tag));

        verdict.Problem.Should().BeNull();
        GitHubWorkflowValidator.Check(ActionYml(repo, tag)).Kind.Should().Be(GitHubWorkflowValidator.FileKind.Action);
    }

    [Theory]
    [InlineData("      - uses: actions/checkout\n")]                   // no ref
    [InlineData("      - uses: \u0430ctions/checkout@v4\n")]           // Cyrillic homoglyph
    [InlineData("      - uses: ${{ inputs.action }}\n")]              // an expression
    [InlineData("      - uses: |\n          actions/checkout@v4\n")]  // a block scalar
    [InlineData("      - name: nothing to do\n")]                     // neither uses nor run
    public void A_step_with_a_malformed_uses_is_invalid(string step)
    {
        var text = Workflow.Replace("      - uses: actions/checkout@v4\n        with:\n          fetch-depth: 0\n", step, StringComparison.Ordinal);

        Validator.Validate(text).Valid.Should().BeFalse();
    }

    [Theory]
    [InlineData("- a\n- b\n")]
    [InlineData("name: neither\n")]
    [InlineData("jobs:\n  b:\n    runs-on: x\n")]
    [InlineData("jobs: [a]\n")]
    public void A_document_that_is_neither_workflow_nor_action_is_invalid(string text)
    {
        Validator.Validate(text).Valid.Should().BeFalse();
    }

    // ═══ ValidateChange ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void A_planned_uses_change_is_the_only_difference_found()
    {
        var after = Workflow.Replace("actions/checkout@v4", "actions/checkout@v5", StringComparison.Ordinal);

        var verdict = GitHubWorkflowValidator.ValidateChange(Workflow, after, Rules(("actions/checkout@v4", "actions/checkout@v5")));

        verdict.Verdict.Valid.Should().BeTrue();
        verdict.Changes.Should().ContainSingle();
        verdict.Changes[0].Should().Be(new WorkflowChange("jobs.build.steps[0].uses", "actions/checkout@v4", "actions/checkout@v5", 7));
    }

    [Fact]
    public void An_unplanned_uses_change_is_refused()
    {
        var after = Workflow.Replace("actions/checkout@v4", "actions/checkout@v5", StringComparison.Ordinal);

        GitHubWorkflowValidator.ValidateChange(Workflow, after, Rules()).Verdict.Valid.Should().BeFalse();
    }

    [Fact]
    public void A_planned_setup_version_is_allowed_only_under_its_setup_action()
    {
        var after = Workflow.Replace("node-version: 20", "node-version: 22", StringComparison.Ordinal);
        var rules = new WorkflowChangeRules(new HashSet<(string, string)>(), new HashSet<(string, string, string)> { ("node-version", "20", "22") });

        GitHubWorkflowValidator.ValidateChange(Workflow, after, rules).Verdict.Valid.Should().BeTrue();

        var elsewhere = Workflow.Replace("actions/setup-node@v4", "someone/setup-node@v4", StringComparison.Ordinal);
        GitHubWorkflowValidator.ValidateChange(elsewhere, elsewhere.Replace("node-version: 20", "node-version: 22", StringComparison.Ordinal), rules)
            .Verdict.Valid.Should().BeFalse();
    }

    [Theory]
    [InlineData("          fetch-depth: 0\n", "          fetch-depth: 1\n")]                 // another with: value
    [InlineData("      - run: npm ci\n", "      - run: npm ci && curl evil\n")]            // a run: value
    [InlineData("    runs-on: ubuntu-latest\n", "    runs-on: self-hosted\n")]            // another key
    [InlineData("      - run: npm ci\n", "      - run: npm ci\n        env: {A: b}\n")]  // an added key
    [InlineData("on: [pull_request]\n", "on: [pull_request, push]\n")]                    // a flow sequence grew
    public void Any_other_difference_is_refused(string from, string to)
    {
        var after = Workflow.Replace(from, to, StringComparison.Ordinal);

        GitHubWorkflowValidator.ValidateChange(Workflow, after, Rules()).Verdict.Valid.Should().BeFalse();
    }

    [Fact]
    public void A_uses_text_inside_a_run_block_is_not_a_uses_value()
    {
        var before = Workflow.Replace("      - run: npm ci\n", "      - run: |\n          uses: actions/checkout@v4\n", StringComparison.Ordinal);
        var after = before.Replace("          uses: actions/checkout@v4\n", "          uses: actions/checkout@v5\n", StringComparison.Ordinal);

        var verdict = GitHubWorkflowValidator.ValidateChange(before, after, Rules(("actions/checkout@v4", "actions/checkout@v5")));

        verdict.Verdict.Valid.Should().BeFalse();
        verdict.Verdict.Problem!.Should().Contain("block scalar");
    }

    [Fact]
    public void A_planned_uses_change_in_flow_style_is_refused()
    {
        const string before = "on: push\njobs:\n  b:\n    runs-on: x\n    steps:\n      - {uses: actions/checkout@v4}\n";

        var verdict = GitHubWorkflowValidator.ValidateChange(before, before.Replace("@v4", "@v5", StringComparison.Ordinal), Rules(("actions/checkout@v4", "actions/checkout@v5")));

        verdict.Verdict.Valid.Should().BeFalse();
        verdict.Verdict.Problem!.Should().Contain("flow style");
    }

    [Fact]
    public void A_planned_change_under_an_anchor_that_an_alias_repeats_is_refused()
    {
        const string before =
            "x-node: &node\n" +
            "  node-version: 20\n" +
            "on: push\n" +
            "jobs:\n" +
            "  b:\n" +
            "    runs-on: x\n" +
            "    steps:\n" +
            "      - uses: actions/setup-node@v4\n" +
            "        with: *node\n";
        var rules = new WorkflowChangeRules(new HashSet<(string, string)>(), new HashSet<(string, string, string)> { ("node-version", "20", "22") });

        var verdict = GitHubWorkflowValidator.ValidateChange(before, before.Replace("node-version: 20", "node-version: 22", StringComparison.Ordinal), rules);

        verdict.Verdict.Valid.Should().BeFalse();
        verdict.Verdict.Problem!.Should().Contain("anchor");
    }

    [Fact]
    public void Editing_an_alias_or_an_anchor_is_refused()
    {
        const string before = "x-a: &a\n  k: 1\nx-b: &b\n  k: 2\non: push\njobs:\n  b:\n    runs-on: x\n    steps:\n      - uses: actions/setup-node@v4\n        with: *a\n";

        GitHubWorkflowValidator.ValidateChange(before, before.Replace("with: *a", "with: *b", StringComparison.Ordinal), Rules()).Verdict.Valid.Should().BeFalse();
        GitHubWorkflowValidator.ValidateChange(before, before.Replace("x-b: &b", "x-b: &c", StringComparison.Ordinal), Rules()).Verdict.Valid.Should().BeFalse();
    }

    // ═══ The target action.yml (real S12 pairs) ═══════════════════════════════════════════════════

    private static WorkflowChangeVerdict BumpUpload(string workflow, string targetTag)
    {
        var after = workflow.Replace("actions/upload-artifact@v3", $"actions/upload-artifact@{targetTag}", StringComparison.Ordinal);
        return GitHubWorkflowValidator.ValidateChange(workflow, after, Rules(("actions/upload-artifact@v3", $"actions/upload-artifact@{targetTag}")) with
        {
            TargetActions = new Dictionary<string, string> { [$"actions/upload-artifact@{targetTag}"] = ActionYml("actions_upload-artifact", targetTag) },
        });
    }

    [Fact]
    public void A_bump_to_an_action_that_declares_every_input_and_output_used_passes()
    {
        var verdict = BumpUpload(Workflow, "v4");

        verdict.Verdict.Problem.Should().BeNull();
        verdict.Verdict.Valid.Should().BeTrue();
    }

    [Fact]
    public void A_referenced_output_the_target_does_not_declare_is_refused()
    {
        // upload-artifact v3 declares no outputs; the workflow reads steps.up.outputs.artifact-id.
        var workflow = Workflow.Replace("actions/upload-artifact@v3", "actions/upload-artifact@v2", StringComparison.Ordinal);
        var after = workflow.Replace("actions/upload-artifact@v2", "actions/upload-artifact@v3", StringComparison.Ordinal);

        var verdict = GitHubWorkflowValidator.ValidateChange(workflow, after, Rules(("actions/upload-artifact@v2", "actions/upload-artifact@v3")) with
        {
            TargetActions = new Dictionary<string, string> { ["actions/upload-artifact@v3"] = ActionYml("actions_upload-artifact", "v3") },
        });

        verdict.Verdict.Valid.Should().BeFalse();
        verdict.Verdict.Problem!.Should().Contain("output");
    }

    [Fact]
    public void An_input_the_target_does_not_declare_is_refused()
    {
        var workflow = Workflow.Replace("          path: dist\n", "          path: dist\n          compression-level: 9\n", StringComparison.Ordinal);

        BumpUpload(workflow, "v4").Verdict.Valid.Should().BeTrue();

        var older = workflow.Replace("actions/upload-artifact@v3", "actions/upload-artifact@v2", StringComparison.Ordinal);
        var verdict = GitHubWorkflowValidator.ValidateChange(older, older.Replace("@v2", "@v3", StringComparison.Ordinal), Rules(("actions/upload-artifact@v2", "actions/upload-artifact@v3")) with
        {
            TargetActions = new Dictionary<string, string> { ["actions/upload-artifact@v3"] = ActionYml("actions_upload-artifact", "v3") },
        });
        verdict.Verdict.Problem!.Should().Contain("does not declare as an input");
    }

    [Fact]
    public void A_required_input_without_a_default_that_the_step_omits_is_refused()
    {
        var workflow = Workflow.Replace("        with:\n          path: dist\n", "        with:\n          name: dist\n", StringComparison.Ordinal);

        var verdict = BumpUpload(workflow, "v4");

        verdict.Verdict.Valid.Should().BeFalse();
        verdict.Verdict.Problem!.Should().Contain("required input");
    }

    [Fact]
    public void A_checkout_major_with_the_same_interface_passes()
    {
        var after = Workflow.Replace("actions/checkout@v4", "actions/checkout@v5", StringComparison.Ordinal);
        var rules = Rules(("actions/checkout@v4", "actions/checkout@v5")) with
        {
            TargetActions = new Dictionary<string, string> { ["actions/checkout@v5"] = ActionYml("actions_checkout", "v5") },
        };

        GitHubWorkflowValidator.ValidateChange(Workflow, after, rules).Verdict.Valid.Should().BeTrue();
    }
}
