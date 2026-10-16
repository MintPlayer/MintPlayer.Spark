using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Git.MergeQueue;
using CodeCoverage.MergeQueue;
using Xunit;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// <c>coverage.yml</c>'s conflict resolution v2 (merge-queue PRD §10.3, §10.6, O35 #1, O36): strategies, validators,
/// regenerate and action per rule, the opt-in presets, the keep-both defaults and their masking, and D57's promise that
/// whatever the parser accepts the git writer accepts.
/// </summary>
public class MergeQueueYmlResolutionTests
{
    private static MergeQueueYmlResult Parse(string body, string action = "postRebaseActionName: regenerate.yml")
        => MergeQueueYml.Parse($"mergeQueue:\n  enabled: true\n  {action}\n{body}", "main");

    [Fact]
    public void The_keep_both_defaults_follow_the_explicit_rules_with_their_source()
    {
        var parsed = Parse("""
              autoResolve:
                - path: "**/App_Data/modelHashes.json"
            """);

        parsed.Errors.Should().BeEmpty();
        var rules = parsed.Section!.AutoResolve;
        rules[0].Path.Should().Be("**/App_Data/modelHashes.json");
        rules[0].Source.Should().Be(MergeQueueYml.Source);
        rules.Skip(1).Select(r => r.Path).Should().Equal(AutoResolvePresets.KeepBothDefaultOrder.SelectMany(g => AutoResolvePresets.KeepBothDefaults[g]).Select(p => p.Path));
        rules.Skip(1).Should().AllSatisfy(r => r.Source.Should().Be(MergeQueueYml.DefaultSource));
        rules.Single(r => r.Path == "**/*.json").Validate.Should().Equal([MergeQueueWireRules.JsonNoDuplicateKeys]);
        rules.Single(r => r.Path == "**/*.csproj").Validate.Should().Equal([MergeQueueWireRules.Xml]);
        parsed.Section.KeepBothDefaults.Should().Equal(["json", "ignoreFiles", "changelog", "xmlProjects"]);
        parsed.Section.Values.Should().Contain(v => v.Key == "autoResolve" && v.Value.StartsWith("**/*.json (keep-both", StringComparison.Ordinal) && v.Source == "default");
    }

    [Fact]
    public void Keep_both_defaults_false_turns_every_group_off_and_a_map_turns_off_one()
    {
        Parse("  keepBothDefaults: false\n").Section!.AutoResolve.Should().BeEmpty();

        var oneOff = Parse("  keepBothDefaults: { json: false }\n").Section!;
        oneOff.AutoResolve.Should().NotContain(r => r.Path == "**/*.json");
        oneOff.AutoResolve.Should().Contain(r => r.Path == "**/.gitignore");
        oneOff.KeepBothDefaults.Should().Equal(["ignoreFiles", "changelog", "xmlProjects"]);

        Parse("  keepBothDefaults: { yaml: false }\n").Errors.Should().ContainSingle().Which.Should().Contain("keepBothDefaults 'yaml'");
    }

    [Fact]
    public void An_explicit_evict_masks_a_default_for_its_path()
    {
        var section = Parse("""
              autoResolve:
                - path: "**/appsettings.json"
                  strategy: evict
            """).Section!;

        var rule = section.AutoResolve.First(r => new Git.PathGlob(r.Path).Matches("src/App/appsettings.json"));
        rule.Strategy.Should().Be(EMergeQueueResolveStrategy.Evict);
        section.AutoResolve.First(r => new Git.PathGlob(r.Path).Matches("src/App/security.json")).Strategy.Should().Be(EMergeQueueResolveStrategy.KeepBoth);
    }

    [Fact]
    public void Presets_expand_after_the_explicit_rules_and_name_their_source()
    {
        var parsed = Parse("""
              autoResolveDefaults: [npm, nuget]
              keepBothDefaults: false
            """);

        parsed.Errors.Should().BeEmpty();
        parsed.Section!.AutoResolve.Select(r => (r.Path, r.Source)).Should().Equal(
            [("**/package-lock.json", "preset npm"), ("**/npm-shrinkwrap.json", "preset npm"), ("**/packages.lock.json", "preset nuget")]);
        parsed.Section.AutoResolve.Should().AllSatisfy(r => r.Regenerate.Should().Be(true));
        parsed.Section.RegeneratedPaths.Should().Equal(AutoResolvePresets.DefaultRegeneratedPaths, "a preset adds no glob; the lockfiles are in the default list (O37)");

        Parse("  autoResolveDefaults: [maven]\n").Errors.Should().Contain(e => e.Contains("autoResolveDefaults 'maven'"));
        Parse("  autoResolveDefaults: [npm]\n", action: "postRebaseWhen: auto-resolved").Errors.Should().Contain(e => e.Contains("postRebaseActionName"));
    }

    [Fact]
    public void Keep_both_regenerates_by_default_only_when_a_workflow_runs_for_it()
    {
        Parse("").Section!.AutoResolve.Single(r => r.Path == "**/*.json").Regenerate.Should().Be(true, "postRebaseActionName is set");
        Parse("", action: "enqueue: { label: merge-queue }").Section!.AutoResolve.Single(r => r.Path == "**/*.json").Regenerate.Should().Be(false, "no workflow exists");
        Parse("  postRebaseWhen: never\n").Section!.AutoResolve.Single(r => r.Path == "**/*.json").Regenerate.Should().Be(false, "the queue runs no workflow");

        var own = Parse("""
              autoResolve:
                - path: "**/security.json"
                  strategy: keep-both
                  regenerate: false
                - path: "go.sum"
                  strategy: line-set
                  action: tidy.yml
            """).Section!.AutoResolve;
        own[0].Regenerate.Should().Be(false);
        own[1].Regenerate.Should().Be(true);
        own[1].Action.Should().Be("tidy.yml");
    }

    [Fact]
    public void A_keep_both_rule_with_no_workflow_needs_no_postRebaseActionName()
        => Parse("""
              autoResolve:
                - path: "**/security.json"
                  strategy: keep-both
                  validate: [json-no-duplicate-keys]
            """, action: "enqueue: { label: merge-queue }").Errors.Should().BeEmpty();

    [Theory]
    [InlineData("strategy: take-base\n      regenerate: false", "take-base")]
    [InlineData("strategy: keep-both\n      keys: [a]", "keys only scope take-base")]
    [InlineData("strategy: keep-both\n      validate: [toml]", "validator 'toml'")]
    [InlineData("strategy: evict\n      validate: [json]", "evict resolves nothing")]
    [InlineData("strategy: keep-both\n      action: ../evil.yml", "action '../evil.yml'")]
    [InlineData("strategy: merge-driver", "merge-driver")]
    public void A_rule_the_writer_would_refuse_is_an_error(string rest, string expected)
        => Parse($"  autoResolve:\n    - path: \"x.json\"\n      {rest}\n").Errors.Should().Contain(e => e.Contains(expected));

    [Fact]
    public void A_regenerate_true_without_a_workflow_is_an_error()
        => Parse("  autoResolve:\n    - path: \"x.json\"\n      strategy: keep-both\n      regenerate: true\n", action: "enqueue: { label: merge-queue }")
            .Errors.Should().ContainSingle().Which.Should().Contain("asks for regenerate");

    /// <summary>§0 D57, extended to §10: every configuration the parser accepts becomes a request the git writer accepts,
    /// through the writer's own rule builder (<see cref="AutoResolveRule.FromWire"/>).</summary>
    public static TheoryData<string> Accepted() => new()
    {
        "",
        "  keepBothDefaults: false\n",
        "  keepBothDefaults: { json: false, xmlProjects: false }\n",
        "  autoResolveDefaults: [spark, npm, pnpm, yarn, nuget]\n",
        "  autoResolve:\n    - path: \"**/App_Data/modelHashes.json\"\n      keys: [modelHash]\n",
        "  autoResolve:\n    - path: \"**/security.json\"\n      strategy: keep-both\n      validate: [json-no-duplicate-keys, utf8]\n      regenerate: false\n",
        "  autoResolve:\n    - path: \"go.sum\"\n      strategy: line-set\n      action: tidy.yml\n",
        "  autoResolve:\n    - path: \"**/appsettings.json\"\n      strategy: evict\n",
        "  postRebaseWhen: always\n  autoResolve:\n    - path: \"**/x.json\"\n      strategy: keep-both\n      validate: [json, no-conflict-markers]\n",
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Whatever_the_parser_accepts_the_writer_accepts(string body)
    {
        var parsed = Parse(body);
        parsed.Errors.Should().BeEmpty(body);

        var section = parsed.Section!;
        section.AutoResolve.Count.Should().BeLessThanOrEqualTo(MergeQueueWireRules.MaxGlobs);
        foreach (var wire in section.AutoResolve.Select(GitWriterAutoResolve.From))
            AutoResolveRule.FromWire(wire.Path, wire.Keys, wire.AnyLine, wire.Strategy, wire.Validate, wire.Regenerate).Should().NotBeNull(wire.Path);
        section.RegeneratedPaths.Count.Should().BeLessThanOrEqualTo(MergeQueueWireRules.MaxGlobs);
        section.RegeneratedPaths.Should().AllSatisfy(p => MergeQueueWireRules.IsGlob(p).Should().BeTrue(p));
        section.Workflows().Should().AllSatisfy(w => MergeQueueWireRules.IsWorkflowFileName(w).Should().BeTrue(w));
    }

    /// <summary>§10.6: a snapshot stored before §10 has no strategy, validators or regenerate, and reads as take-base.</summary>
    [Fact]
    public void An_old_snapshot_rule_reads_as_take_base_and_regenerates()
    {
        var rule = JsonSerializer.Deserialize<MergeQueueAutoResolveRule>("""{"Path":"**/App_Data/modelHashes.json","Keys":[]}""")!;

        rule.Strategy.Should().Be(EMergeQueueResolveStrategy.TakeBase);
        rule.Validate.Should().BeEmpty();
        rule.Regenerate.HasValue.Should().BeFalse();
        rule.Regenerates().Should().BeTrue();
        GitWriterAutoResolve.From(rule).Should().Be(new GitWriterAutoResolve("**/App_Data/modelHashes.json", AnyLine: true));
    }

    [Fact]
    public void The_check_lists_each_resolution_with_its_strategy_and_each_regenerate_run()
    {
        var entry = new MergeQueueEntry
        {
            State = EMergeQueueEntryState.Regenerating,
            EnqueuedBy = "maintainer",
            PinnedHeadSha = "1111111111111111111111111111111111111111",
            ResolvedPaths = ["a/security.json", "a/modelHashes.json"],
            Resolutions =
            [
                new() { Path = "a/security.json", Strategy = "keep-both", Regenerate = false, Rule = 1 },
                new() { Path = "a/modelHashes.json", Strategy = "take-base", Regenerate = true, Rule = 0 },
            ],
            Dispatches =
            [
                new() { Workflow = "lockfiles.yml", RunId = 1, RunUrl = "https://github.com/acme/widget/actions/runs/1", Finished = true, Succeeded = true },
                new() { Workflow = "regenerate.yml", RunId = 2, RunUrl = "https://github.com/acme/widget/actions/runs/2" },
            ],
        };

        var summary = MergeQueueCheckRenderer.Render(entry, null, null).Summary;

        summary.Should().Contain("- `a/security.json`: keep-both\n".Replace("\n", Environment.NewLine));
        summary.Should().Contain("- `a/modelHashes.json`: take-base, regenerated");
        summary.Should().Contain("| Regenerate run | `lockfiles.yml` succeeded: https://github.com/acme/widget/actions/runs/1 |");
        summary.Should().Contain("| Regenerate run | `regenerate.yml` running: https://github.com/acme/widget/actions/runs/2 |");
    }

    // ── regeneratedPaths: the default list, its replacement, and no widening by a default's glob (O37) ─────

    [Fact]
    public void Without_regeneratedPaths_the_default_list_applies_and_says_so()
    {
        var section = Parse("").Section!;

        section.RegeneratedPaths.Should().Equal(AutoResolvePresets.DefaultRegeneratedPaths);
        section.RegeneratedPaths.Should().Equal(
            ["**/App_Data/modelHashes.json", "**/App_Data/securityPosture.txt", "**/package-lock.json", "**/npm-shrinkwrap.json", "**/yarn.lock", "**/pnpm-lock.yaml", "**/packages.lock.json"]);
        section.Values.Should().ContainSingle(v => v.Key == "regeneratedPaths").Which.Source.Should().Be(MergeQueueYml.DefaultSource);
    }

    [Fact]
    public void A_regeneratedPaths_in_the_file_replaces_the_default_list_entirely()
    {
        var section = Parse("  regeneratedPaths: [\"**/App_Data/Model/*.json\"]\n").Section!;

        section.RegeneratedPaths.Should().Equal(["**/App_Data/Model/*.json"], "the file's list replaces the defaults, it is not merged into them");
        section.Values.Should().ContainSingle(v => v.Key == "regeneratedPaths").Which.Source.Should().Be(MergeQueueYml.Source);
        var snapshot = section.Snapshot("master@abc", new Repository(), null);
        snapshot.Values.Should().ContainSingle(v => v.Key == "regeneratedPaths").Which.Source.Should().Be("coverage.yml @ master@abc");
    }

    [Fact]
    public void An_empty_regeneratedPaths_is_kept_empty_and_is_an_error_only_when_a_workflow_can_dispatch()
    {
        // The keep-both defaults regenerate with postRebaseActionName set, so a workflow can run.
        var dispatching = Parse("  regeneratedPaths: []\n");
        dispatching.Section!.RegeneratedPaths.Should().BeEmpty();
        dispatching.Errors.Should().ContainSingle().Which.Should().Contain("regeneratedPaths is empty");

        // No rule regenerates and the queue runs no workflow on its own: nothing is dispatched, so nothing is refused.
        var idle = Parse("  regeneratedPaths: []\n  keepBothDefaults: false\n");
        idle.Errors.Should().BeEmpty();
        idle.Section!.RegeneratedPaths.Should().BeEmpty();
    }

    [Fact]
    public void A_default_or_preset_rule_never_adds_its_glob_but_an_explicit_regenerating_rule_does()
    {
        var section = Parse("""
              regeneratedPaths: ["**/App_Data/modelHashes.json"]
              autoResolveDefaults: [nuget]
              autoResolve:
                - path: "**/App_Data/translations.json"
                  strategy: keep-both
                  regenerate: true
            """).Section!;

        section.AutoResolve.Should().Contain(r => r.Path == "**/*.json" && r.Regenerate == true, "the keep-both default regenerates here");
        section.AutoResolve.Should().Contain(r => r.Path == "**/*.csproj" && r.Regenerate == true);
        section.RegeneratedPaths.Should().Equal(["**/App_Data/modelHashes.json", "**/App_Data/translations.json"]);
        section.RegeneratedPaths.Should().NotContain("**/*.json");
        section.RegeneratedPaths.Should().NotContain("**/*.csproj");
        section.RegeneratedPaths.Should().NotContain("**/packages.lock.json");
        Parse("").Section!.RegeneratedPaths.Should().NotContain("**/*.json", "the default list is not widened by the keep-both defaults either");
    }

    // ── The globs a publish sends the writer: the list plus the concrete files a default resolved (O37) ──

    private static MergeQueueConfigSnapshot PublishConfig(params string[] regeneratedPaths) => new()
    {
        PostRebaseActionName = "regenerate.yml",
        RegeneratedPaths = [.. regeneratedPaths],
        AutoResolve =
        [
            new() { Path = "**/App_Data/translations.json", Strategy = EMergeQueueResolveStrategy.KeepBoth, Regenerate = true, Source = "coverage.yml @ master" },
            new() { Path = "**/packages.lock.json", Strategy = EMergeQueueResolveStrategy.TakeBase, Regenerate = true, Source = "preset nuget" },
            new() { Path = "**/*.json", Strategy = EMergeQueueResolveStrategy.KeepBoth, Regenerate = true, Source = MergeQueueYml.DefaultSource },
            new() { Path = "**/.gitignore", Strategy = EMergeQueueResolveStrategy.KeepBoth, Regenerate = false, Source = MergeQueueYml.DefaultSource },
        ],
    };

    [Fact]
    public void A_regenerating_default_or_preset_resolution_adds_its_concrete_file_only()
    {
        var config = PublishConfig("**/App_Data/modelHashes.json");

        MergeQueueWorkflowGuard.PublishRegeneratedPaths(config,
        [
            Resolution("App/security.json", 2, true),
            Resolution("src/A/packages.lock.json", 1, true),
            Resolution("App/App_Data/translations.json", 0, true),
            Resolution(".gitignore", 3, false),
            Resolution("App/unknown.json", -1, true),
            Resolution("App/security.json", 2, true),
        ]).Should().Equal(["**/App_Data/modelHashes.json", "App/security.json", "src/A/packages.lock.json"],
            "coverage.yml's own rule has its glob in the list, a non-regenerating or unknown rule adds nothing, and a path is added once");
    }

    [Fact]
    public void A_resolved_file_the_list_already_allows_takes_no_entry()
        => MergeQueueWorkflowGuard.PublishRegeneratedPaths(PublishConfig(AutoResolvePresets.DefaultRegeneratedPaths.ToArray()), [Resolution("src/A/packages.lock.json", 1, true)])
            .Should().Equal(AutoResolvePresets.DefaultRegeneratedPaths);

    [Theory]
    [InlineData("App/*.json")]
    [InlineData("App/s?curity.json")]
    [InlineData("App/[ab].json")]
    public void A_resolved_path_with_a_glob_character_is_not_added(string path)
        => MergeQueueWorkflowGuard.PublishRegeneratedPaths(PublishConfig("**/x"), [Resolution(path, 2, true)]).Should().Equal(["**/x"]);

    [Fact]
    public void Without_resolutions_the_snapshots_list_is_sent_as_it_is()
        => MergeQueueWorkflowGuard.PublishRegeneratedPaths(PublishConfig("**/App_Data/modelHashes.json", "**/*.json"), null)
            .Should().Equal(["**/App_Data/modelHashes.json", "**/*.json"], "an entry prepared before §10.6 (or O37) keeps its stored list");

    [Fact]
    public void More_entries_than_the_writer_takes_is_null()
    {
        var full = PublishConfig([.. Enumerable.Range(0, MergeQueueWireRules.MaxGlobs - 1).Select(i => $"g{i}/**")]);
        MergeQueueWorkflowGuard.PublishRegeneratedPaths(full, [Resolution("a.json", 2, true)]).Should().HaveCount(MergeQueueWireRules.MaxGlobs);
        MergeQueueWorkflowGuard.PublishRegeneratedPaths(full, [Resolution("a.json", 2, true), Resolution("b.json", 2, true)]).Should().BeNull();
    }

    // ── Which workflows a prepared entry runs (O36) ──────────────────────────────────────────────────

    private static MergeQueueConfigSnapshot Config(EPostRebaseWhen when = EPostRebaseWhen.AutoResolved) => new()
    {
        PostRebaseActionName = "regenerate.yml",
        PostRebaseWhen = when,
        AutoResolve =
        [
            new() { Path = "**/modelHashes.json", Strategy = EMergeQueueResolveStrategy.TakeBase, Regenerate = true },
            new() { Path = "**/security.json", Strategy = EMergeQueueResolveStrategy.KeepBoth, Regenerate = false },
            new() { Path = "**/packages.lock.json", Strategy = EMergeQueueResolveStrategy.TakeBase, Regenerate = true, Action = "lockfiles.yml" },
        ],
    };

    private static MergeQueueResolution Resolution(string path, int rule, bool regenerate) => new() { Path = path, Rule = rule, Regenerate = regenerate, Strategy = "x" };

    [Fact]
    public void Keep_both_alone_needs_no_workflow_and_mixed_resolutions_need_each_rules_workflow_once_in_order()
    {
        MergeQueueWorkflowGuard.RegenerateWorkflows(Config(), [Resolution("a/security.json", 1, false)], ["a/security.json"]).Should().BeEmpty();
        MergeQueueWorkflowGuard.RegenerateWorkflows(Config(), [Resolution("a/modelHashes.json", 0, true), Resolution("b/modelHashes.json", 0, true), Resolution("a/packages.lock.json", 2, true)],
            ["a/modelHashes.json", "b/modelHashes.json", "a/packages.lock.json"]).Should().Equal(["lockfiles.yml", "regenerate.yml"]);
    }

    [Fact]
    public void Without_resolutions_every_resolved_path_regenerates_with_postRebaseActionName()
    {
        MergeQueueWorkflowGuard.RegenerateWorkflows(Config(), null, ["a/security.json"]).Should().Equal(["regenerate.yml"]);
        MergeQueueWorkflowGuard.RegenerateWorkflows(Config(), null, []).Should().BeEmpty();
    }

    [Fact]
    public void Always_runs_every_configured_workflow_and_never_runs_none()
    {
        MergeQueueWorkflowGuard.RegenerateWorkflows(Config(EPostRebaseWhen.Always), [], []).Should().Equal(["lockfiles.yml", "regenerate.yml"]);
        MergeQueueWorkflowGuard.RegenerateWorkflows(Config(EPostRebaseWhen.Never), [Resolution("a/modelHashes.json", 0, true)], ["a/modelHashes.json"]).Should().BeEmpty();
    }
}
