using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using Xunit;
using static CodeCoverage.Tests.MergeQueue.MergeQueueHarness;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// The fork precondition of a dispatch (merge-queue PRD §0 D65), over workflow files the way GitHub reads them: when a
/// push that creates <c>mq/master/pr-7</c> would start a workflow, what the regenerate workflow may do, and what its runs
/// start. Anything the guard cannot evaluate with certainty must come out unsafe.
/// </summary>
public class MergeQueueForkGuardTests
{
    private const string QueueBranch = "mq/master/pr-7";

    private static IReadOnlyList<ForkGuardFinding> Evaluate(IEnumerable<ForgeWorkflowFile>? pushed = null, string? regenerate = null,
        IEnumerable<ForgeWorkflowFile>? alsoAtDefault = null, string queueBranch = QueueBranch)
        => MergeQueueForkGuard.Evaluate(
            [new(RegeneratePath, SafeRegenerate), .. pushed ?? []], "master",
            [new(RegeneratePath, regenerate ?? SafeRegenerate), .. alsoAtDefault ?? []], "master",
            "regenerate.yml", queueBranch);

    [Fact]
    public void The_setup_guides_regenerate_workflow_alone_is_safe()
        => Evaluate().Should().BeEmpty();

    /// <summary>
    /// Dogfood: this repository's own workflows (every <c>push:</c> trigger is <c>branches: [master]</c>, D37) and its
    /// regenerate workflow, and the setup guide's template, pass the fork precondition as they are.
    /// </summary>
    [Fact]
    public void This_repositorys_own_workflows_and_the_template_pass_the_fork_precondition()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "coverage.yml")))
            root = root.Parent;
        root.Should().NotBeNull();
        var workflows = Directory.GetFiles(Path.Combine(root!.FullName, ".github", "workflows"))
            .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .Select(f => new ForgeWorkflowFile($".github/workflows/{Path.GetFileName(f)}", File.ReadAllText(f)))
            .ToList();
        workflows.Should().Contain(f => f.Path == RegeneratePath);

        MergeQueueForkGuard.Evaluate(workflows, "master", workflows, "master", "regenerate.yml", QueueBranch).Should().BeEmpty();

        var template = File.ReadAllText(Path.Combine(root.FullName, "docs", "code-coverage", "templates", "regenerate.yml"));
        Evaluate(regenerate: template).Should().BeEmpty();

        // O36: a rule's own action (the lockfile template) is dispatched as well, so it must pass the precondition too,
        // as the dispatched workflow and beside the regenerate one.
        var lockfiles = new ForgeWorkflowFile(".github/workflows/lockfiles-regenerate.yml",
            File.ReadAllText(Path.Combine(root.FullName, "docs", "code-coverage", "templates", "lockfiles-regenerate.yml")));
        List<ForgeWorkflowFile> both = [new(RegeneratePath, template), lockfiles];
        MergeQueueForkGuard.Evaluate(both, "master", both, "master", "lockfiles-regenerate.yml", QueueBranch).Should().BeEmpty();
        MergeQueueForkGuard.Evaluate(both, "master", both, "master", "regenerate.yml", QueueBranch).Should().BeEmpty();
    }

    // ── (1) What a push of the queue branch starts ─────────────────────────────────────────────────

    /// <summary>GitHub's push trigger, for a push that creates <c>mq/master/pr-7</c>: the branch filters decide, a
    /// tags-only trigger skips branch pushes, and <c>paths</c> never help (the fork chooses the pushed paths).</summary>
    [Theory]
    [InlineData("on: push", true)]
    [InlineData("on: [push, pull_request]", true)]
    [InlineData("on:\n  push:\n", true)]
    [InlineData("on:\n  push: {}\n", true)]
    [InlineData("on:\n  Push:\n", true)]
    [InlineData("on:\n  push:\n    branches: [main]\n", false)]
    [InlineData("on:\n  push:\n    branches: main\n", false)]
    [InlineData("on:\n  push:\n    branches: ['**']\n", true)]
    [InlineData("on:\n  push:\n    branches: ['mq/**']\n", true)]
    [InlineData("on:\n  push:\n    branches: ['mq/*']\n", false)]
    [InlineData("on:\n  push:\n    branches: ['MQ/**']\n", true)]
    [InlineData("on:\n  push:\n    branches: ['refs/heads/**']\n", true)]
    [InlineData("on:\n  push:\n    branches: ['m?q/**']\n", true)]
    [InlineData("on:\n  push:\n    branches: ['**', '!mq/**']\n", false)]
    [InlineData("on:\n  push:\n    branches: ['!mq/**', '**']\n", true)]
    [InlineData("on:\n  push:\n    branches: ['!mq/**']\n", true)]
    [InlineData("on:\n  push:\n    branches-ignore: ['mq/**']\n", false)]
    [InlineData("on:\n  push:\n    branches-ignore: ['mq/*']\n", true)]
    [InlineData("on:\n  push:\n    branches-ignore: ['MQ/**']\n", true)]
    [InlineData("on:\n  push:\n    branches-ignore: ['refs/heads/mq/**']\n", true)]
    [InlineData("on:\n  push:\n    branches-ignore: ['mq/[a-z]*/**']\n", false)]
    [InlineData("on:\n  push:\n    branches-ignore: ['mq/m+aster/pr-[0-9]*']\n", false)]
    [InlineData("on:\n  push:\n    branches-ignore: ['mq/(master)/**']\n", true)]
    [InlineData("on:\n  push:\n    branches-ignore: ['mq/[^x]*/**']\n", true)]
    [InlineData("on:\n  push:\n    branches-ignore: ['!mq/**']\n", true)]
    [InlineData("on:\n  push:\n    branches-ignore:\n", true)]
    [InlineData("on:\n  push:\n    tags: ['v*']\n", false)]
    [InlineData("on:\n  push:\n    tags-ignore: ['v*']\n", false)]
    [InlineData("on:\n  push:\n    paths: ['src/**']\n", true)]
    [InlineData("on:\n  push:\n    branches: ['**']\n    paths: ['docs/**']\n", true)]
    [InlineData("on:\n  push:\n    branches: [main]\n    paths: ['src/**']\n", false)]
    [InlineData("on:\n  push:\n    branches: [main]\n    branches-ignore: [x]\n", true)]
    [InlineData("on:\n  push:\n    branch: [main]\n", true)]
    [InlineData("on: create", true)]
    [InlineData("on:\n  create:\n  push:\n    branches: [main]\n", true)]
    [InlineData("on: workflow_dispatch", false)]
    [InlineData("on:\n  pull_request:\n    branches: ['**']\n  pull_request_target:\n", false)]
    [InlineData("on:\n  schedule:\n    - cron: '0 0 * * *'\n", false)]
    public void A_push_trigger_runs_on_the_queue_branch_unless_its_branch_filter_leaves_it_out(string on, bool unsafeForForks)
    {
        var findings = Evaluate(pushed: [new(".github/workflows/ci.yml", $"name: ci\n{on}\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: make\n")]);

        if (unsafeForForks)
        {
            var finding = findings.Should().ContainSingle().Which;
            finding.Path.Should().Be(".github/workflows/ci.yml");
            finding.Ref.Should().Be("master");
            finding.Line.HasValue.Should().BeTrue("the finding points at the trigger's line");
        }
        else
            findings.Should().BeEmpty();
    }

    /// <summary>A queue branch under a base with a <c>/</c> in it: <c>*</c> stops at the slash, <c>**</c> does not, and
    /// a <c>.</c> is literal where it keeps a workflow from running.</summary>
    [Theory]
    [InlineData("branches-ignore: ['mq/**']", false)]
    [InlineData("branches-ignore: ['mq/*/pr-*']", true)]
    [InlineData("branches-ignore: ['mq/release/1.0/**']", false)]
    [InlineData("branches-ignore: ['mq/release/1x0/**']", true)]
    [InlineData("branches: ['release/1.0']", false)]
    [InlineData("branches: ['mq/release/*/pr-3']", true)]
    [InlineData("branches: ['mq/*/pr-3']", false)]
    [InlineData("branches: ['mq/release/**']", true)]
    public void A_base_branch_with_a_slash_is_matched_the_way_GitHub_matches_it(string filter, bool unsafeForForks)
    {
        var findings = Evaluate(pushed: [new(".github/workflows/ci.yml", $"on:\n  push:\n    {filter}\n")], queueBranch: "mq/release/1.0/pr-3");

        (findings.Count > 0).Should().Be(unsafeForForks, string.Join("; ", findings));
    }

    /// <summary>Anything the guard cannot read is unsafe: an unreadable file, broken YAML, a duplicate key, several
    /// documents, a merge key, no <c>on:</c>.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("on: [push\n")]
    [InlineData("on: push\non: pull_request\n")]
    [InlineData("on: workflow_dispatch\n---\non: push\n")]
    [InlineData("base: &base {branches: [main]}\non:\n  push:\n    <<: *base\n")]
    [InlineData("name: no triggers\njobs: {}\n")]
    [InlineData("- on: push\n")]
    public void A_workflow_the_guard_cannot_read_counts_as_unsafe(string? content)
    {
        var file = content is null ? new ForgeWorkflowFile(".github/workflows/odd.yml", null, "a symlink, not a file") : new ForgeWorkflowFile(".github/workflows/odd.yml", content);

        var findings = Evaluate(pushed: [file]);

        findings.Should().ContainSingle().Which.Path.Should().Be(".github/workflows/odd.yml");
    }

    [Fact]
    public void An_alias_is_followed_and_a_document_of_aliases_does_not_explode()
    {
        var laughs = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"l{i}: &l{i} [*l{i - 1}, *l{i - 1}, *l{i - 1}]")).Replace("*l0", "x", StringComparison.Ordinal);
        var findings = Evaluate(pushed: [new(".github/workflows/ci.yml", $"filters: &ignore ['mq/**']\n{laughs}\non:\n  push:\n    branches-ignore: *ignore\n")]);

        findings.Should().BeEmpty();
    }

    // ── (2) The regenerate workflow ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("no permissions", "permissions:\n  contents: read\n", "")]
    [InlineData("read-all", "permissions:\n  contents: read\n", "permissions: read-all\n")]
    [InlineData("write-all", "permissions:\n  contents: read\n", "permissions: write-all\n")]
    [InlineData("contents: write", "  contents: read\n", "  contents: write\n")]
    [InlineData("id-token", "  contents: read\n", "  contents: read\n  id-token: write\n")]
    [InlineData("actions: read", "  contents: read\n", "  contents: read\n  actions: read\n")]
    [InlineData("an unknown level", "  contents: read\n", "  contents: maybe\n")]
    [InlineData("a secret in a step", "          persist-credentials: false", "          persist-credentials: false\n          token: ${{ secrets.NPM_TOKEN }}")]
    [InlineData("the whole secrets context", "          persist-credentials: false", "          persist-credentials: false\n          token: ${{ toJSON(secrets) }}")]
    [InlineData("a secret in a condition", "      - uses: actions/checkout@v7", "      - if: secrets.NPM_TOKEN != ''\n        run: echo\n      - uses: actions/checkout@v7")]
    [InlineData("self-hosted", "    runs-on: ubuntu-latest\n", "    runs-on: self-hosted\n")]
    [InlineData("self-hosted in a list", "    runs-on: ubuntu-latest\n", "    runs-on: [self-hosted, linux, x64]\n")]
    [InlineData("a custom label", "    runs-on: ubuntu-latest\n", "    runs-on: build-box\n")]
    [InlineData("a runner group", "    runs-on: ubuntu-latest\n", "    runs-on:\n      group: big\n      labels: [ubuntu-latest]\n")]
    [InlineData("an expression", "    runs-on: ubuntu-latest\n", "    runs-on: ${{ inputs.runner }}\n")]
    [InlineData("an environment", "    runs-on: ubuntu-latest\n", "    runs-on: ubuntu-latest\n    environment: production\n")]
    [InlineData("a reusable workflow", "    runs-on: ubuntu-latest\n    steps:", "    uses: acme/shared/.github/workflows/build.yml@v1\n    secrets: inherit\n    with:\n      x: y\n    steps:")]
    public void A_regenerate_workflow_that_grants_or_reads_more_than_its_template_is_unsafe(string what, string replace, string with)
    {
        var regenerate = SafeRegenerate.Replace(replace, with, StringComparison.Ordinal);
        regenerate.Should().NotBe(SafeRegenerate, $"the {what} variant must change the template");

        var findings = Evaluate(regenerate: regenerate);

        findings.Should().NotBeEmpty(what);
        findings.Should().OnlyContain(f => f.Path == RegeneratePath);
    }

    [Theory]
    [InlineData("permissions: {}", "permissions:\n  contents: read\n", "permissions: {}\n")]
    [InlineData("contents: none", "  contents: read\n", "  contents: none\n  id-token: none\n")]
    [InlineData("job-level permissions only", "permissions:\n  contents: read\n", "")]
    [InlineData("a GitHub-hosted image", "    runs-on: ubuntu-latest\n", "    runs-on: [windows-2022]\n")]
    [InlineData("labels in a mapping", "    runs-on: ubuntu-latest\n", "    runs-on:\n      labels: [ubuntu-24.04-arm]\n")]
    [InlineData("the word in a comment", "    steps:\n", "    steps:\n      # Never add secrets here.\n")]
    [InlineData("an input named like it", "          persist-credentials: false", "          persist-credentials: false\n          token: ${{ inputs.secrets_flag }}")]
    public void A_regenerate_workflow_within_its_template_is_safe(string what, string replace, string with)
    {
        var regenerate = SafeRegenerate.Replace(replace, with, StringComparison.Ordinal);
        if (what == "job-level permissions only")
            regenerate = regenerate.Replace("    runs-on: ubuntu-latest\n", "    runs-on: ubuntu-latest\n    permissions:\n      contents: read\n", StringComparison.Ordinal);
        regenerate.Should().NotBe(SafeRegenerate, $"the {what} variant must change the template");

        Evaluate(regenerate: regenerate).Should().BeEmpty(what);
    }

    [Fact]
    public void A_job_that_overrides_the_top_level_permissions_is_judged_by_its_own()
    {
        var regenerate = SafeRegenerate.Replace("    runs-on: ubuntu-latest\n", "    runs-on: ubuntu-latest\n    permissions:\n      pull-requests: write\n", StringComparison.Ordinal);

        Evaluate(regenerate: regenerate).Should().ContainSingle().Which.Problem.Should().Contain("pull-requests: write");
    }

    [Fact]
    public void A_missing_regenerate_workflow_is_a_finding()
    {
        var findings = MergeQueueForkGuard.Evaluate([], "master", [], "master", "regenerate.yml", QueueBranch);

        findings.Should().ContainSingle().Which.Path.Should().Be(RegeneratePath);
    }

    // ── (3) What the regenerate workflow's runs start ─────────────────────────────────────────────

    [Theory]
    [InlineData("workflows: [regenerate]", true)]
    [InlineData("workflows: [Regenerate]\n    types: [completed]", true)]
    [InlineData("workflows: ['.github/workflows/regenerate.yml']", true)]
    [InlineData("workflows: ['regen*']", true)]
    [InlineData("workflows: [build]", false)]
    [InlineData("workflows: [regenerate]\n    branches-ignore: ['mq/**']", false)]
    [InlineData("workflows: [regenerate]\n    branches: [master]", false)]
    [InlineData("workflows: [regenerate]\n    branches: ['**']", true)]
    [InlineData("types: [completed]", true)]
    public void A_workflow_run_that_follows_the_regenerate_workflow_on_the_queue_branch_is_unsafe(string filters, bool unsafeForForks)
    {
        var chained = new ForgeWorkflowFile(".github/workflows/report.yml", $"on:\n  workflow_run:\n    {filters}\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n");

        var findings = Evaluate(alsoAtDefault: [chained]);

        if (unsafeForForks)
            findings.Should().ContainSingle().Which.Path.Should().Be(".github/workflows/report.yml");
        else
            findings.Should().BeEmpty();
    }

    [Fact]
    public void An_unnamed_regenerate_workflow_is_named_by_its_path()
    {
        var unnamed = SafeRegenerate.Replace("name: regenerate\n", "", StringComparison.Ordinal);
        var byName = new ForgeWorkflowFile(".github/workflows/a.yml", "on:\n  workflow_run:\n    workflows: [regenerate]\n");
        var byPath = new ForgeWorkflowFile(".github/workflows/b.yml", "on:\n  workflow_run:\n    workflows: ['.github/workflows/regenerate.yml']\n");

        var findings = Evaluate(regenerate: unnamed, alsoAtDefault: [byName, byPath]);

        findings.Should().ContainSingle().Which.Path.Should().Be(".github/workflows/b.yml");
    }

    // ── (4) What the queue commit's checks, statuses and deployments start (O25) ──────────────────────────

    /// <summary>
    /// O25: a check run, a check suite, a commit status or a deployment of the <c>mq/</c> commit starts these workflows
    /// with this repository's secrets, and a job that checks out the commit the event names runs the fork's code. The
    /// guard cannot tell whether a job does, so it counts every such trigger as unsafe, whatever its activity types, in
    /// both reads: the base commit's workflows and the default branch's.
    /// </summary>
    [Theory]
    [InlineData("on: check_run", 1)]
    [InlineData("on: [pull_request, check_suite]", 1)]
    [InlineData("on:\n  check_run:\n    types: [completed]", 2)]
    [InlineData("on:\n  check_suite:\n    types: [completed, requested, rerequested]", 2)]
    [InlineData("on:\n  status:", 2)]
    [InlineData("on:\n  deployment_status:", 2)]
    [InlineData("on:\n  deployment:", 2)]
    [InlineData("on:\n  Status:", 2)]
    public void A_workflow_the_queue_commits_checks_statuses_or_deployments_start_is_unsafe_in_both_reads(string on, int line)
    {
        var trigger = on.Split([' ', '\n', '[', ']', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.TrimEnd(':').ToLowerInvariant())
            .First(t => t is "check_run" or "check_suite" or "status" or "deployment_status" or "deployment");
        var file = new ForgeWorkflowFile(".github/workflows/notify.yml", $"{on}\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n");

        foreach (var findings in new[] { Evaluate(pushed: [file]), Evaluate(alsoAtDefault: [file]) })
        {
            var finding = findings.Should().ContainSingle().Which;
            finding.Path.Should().Be(".github/workflows/notify.yml");
            finding.Line.Should().Be(line, "the finding points at the trigger");
            finding.Problem.Should().StartWith($"`on: {trigger}`");
            finding.Problem.Should().Contain(QueueBranch);
            finding.Fix.Should().Contain("check out");
        }
    }

    /// <summary>A workflow read at both refs is one finding when the base branch is the default branch and the file is the
    /// same, and one per ref otherwise.</summary>
    [Fact]
    public void A_trigger_found_in_both_reads_is_listed_once_per_ref()
    {
        var file = new ForgeWorkflowFile(".github/workflows/notify.yml", "on: status\n");

        MergeQueueForkGuard.Evaluate([file], "master", [new(RegeneratePath, SafeRegenerate), file], "master", "regenerate.yml", QueueBranch)
            .Should().ContainSingle();
        MergeQueueForkGuard.Evaluate([file], "release", [new(RegeneratePath, SafeRegenerate), file], "master", "regenerate.yml", "mq/release/pr-7")
            .Select(f => f.Ref).Should().Equal(["release", "master"]);
    }

    // ── The filter patterns themselves ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("mq/*", "mq/master", true)]
    [InlineData("mq/*", "mq/master/pr-7", false)]
    [InlineData("mq/**", "mq/master/pr-7", true)]
    [InlineData("**", "anything/at/all", true)]
    [InlineData("pr-?7", "pr-7", true)]
    [InlineData("pr-x?7", "pr-7", true)]
    [InlineData("pr-x?7", "pr-xx7", false)]
    [InlineData("pr-7+", "pr-777", true)]
    [InlineData("pr-[0-9]", "pr-7", true)]
    [InlineData("pr-[a-z]", "pr-7", false)]
    [InlineData("a\\*b", "a*b", true)]
    [InlineData("a\\*b", "axb", false)]
    public void A_filter_pattern_matches_the_way_GitHub_documents_it(string pattern, string branch, bool matches)
    {
        MergeQueueForkGuard.FilterPattern.Matches(pattern, branch, generous: false).Should().Be(matches);
        MergeQueueForkGuard.FilterPattern.Matches(pattern, branch, generous: true).Should().Be(matches);
    }

    [Theory]
    [InlineData("*?")]
    [InlineData("?x")]
    [InlineData("a++")]
    [InlineData("[]")]
    [InlineData("[a-]")]
    [InlineData("[z-a]")]
    [InlineData("[a-Z]")]
    [InlineData("a]")]
    [InlineData("(a)")]
    [InlineData("a{1,2}")]
    [InlineData("a|b")]
    [InlineData("^a")]
    [InlineData("a$")]
    [InlineData("a\\")]
    [InlineData("a b")]
    [InlineData("")]
    public void A_pattern_outside_the_cheat_sheet_is_not_evaluated(string pattern)
    {
        MergeQueueForkGuard.FilterPattern.Matches(pattern, "mq/master/pr-7", generous: false).HasValue.Should().BeFalse();
        MergeQueueForkGuard.FilterPattern.Matches(pattern, "mq/master/pr-7", generous: true).HasValue.Should().BeFalse();
    }

    [Theory]
    [InlineData("MQ/**", "mq/master", false, true)]
    [InlineData("refs/heads/mq/**", "mq/master", false, true)]
    [InlineData("mq.master", "mq/master", false, true)]
    [InlineData("mq.master", "mq.master", true, true)]
    public void A_pattern_that_makes_a_workflow_run_is_matched_generously_and_one_that_stops_it_strictly(string pattern, string branch, bool strict, bool generous)
    {
        MergeQueueForkGuard.FilterPattern.Matches(pattern, branch, generous: false).Should().Be(strict);
        MergeQueueForkGuard.FilterPattern.Matches(pattern, branch, generous: true).Should().Be(generous);
    }
}
