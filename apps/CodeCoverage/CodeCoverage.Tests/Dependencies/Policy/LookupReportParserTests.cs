using System.Text.Json.Nodes;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Git.Dependencies;
using Xunit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// R1 records from REAL Renovate reports (S12): the record mapping, the lookup document parse, and what the policy does
/// with records it cannot read (PRD §13.11.1 "R1 records").
/// </summary>
public class LookupReportParserTests
{
    public static TheoryData<string> Samples()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.GetDirectories(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Dependencies", "renovate")).Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(dir));
        return data;
    }

    [Fact]
    public void Every_S12_sample_is_here()
        => Samples().Count.Should().Be(14, "the 14 S12 captures, this repository's own included");

    /// <summary>
    /// The runner turned five of the reports into contract fixtures with its own <c>mapReport()</c>; the C# port must give
    /// the very same records, or one side drifted and the replay below would test the wrong thing.
    /// </summary>
    [Theory]
    [InlineData("npm")]
    [InlineData("nuget")]
    [InlineData("maven")]
    [InlineData("go")]
    [InlineData("docker-actions-platform")]
    public void The_port_maps_a_real_report_exactly_as_the_runner_did(string sample)
    {
        var mapped = RenovateReportMapper.Map(DependencyTestKit.RenovateReport(sample));
        var runner = JsonNode.Parse(DependencyTestKit.ContractFixture("valid", $"lookup.{sample}.json"))!["records"]!;

        JsonNode.DeepEquals(JsonNode.Parse(DependencyContract.Serialize(mapped)), runner).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_dependency_and_every_candidate_of_a_real_report_becomes_a_record_never_dropped(string sample)
    {
        var json = DependencyTestKit.RenovateReport(sample);
        var report = JsonNode.Parse(json)!;
        var deps = report["repositories"]!.AsObject().SelectMany(r => r.Value!["packageFiles"]?.AsObject() ?? new JsonObject())
            .SelectMany(m => m.Value!.AsArray()).SelectMany(f => f!["deps"]!.AsArray())
            .Where(d => d!["depName"]?.GetValue<string>() is { Length: > 0 }).ToList();

        var records = RenovateReportMapper.Map(json);

        records.Should().HaveCount(deps.Count);
        records.Sum(r => r.Candidates.Count).Should().Be(deps.Sum(d => d!["updates"]?.AsArray().Count ?? 0));
        records.Where(r => r.Candidates.Any(c => c.UpdateType is null)).Should().OnlyContain(r => r.Hold, "R1: a record missing an updateType is a hold");
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void A_real_report_mapped_and_wrapped_is_a_valid_lookup_document(string sample)
    {
        var document = RenovateReportMapper.Document(DependencyTestKit.RenovateReport(sample), DependencyTestKit.Sha);

        var parse = LookupReportParser.Parse(DependencyContract.Serialize(document), DependencyTestKit.Sha);

        parse.Errors.Should().BeEmpty();
        parse.Document!.Records.Should().HaveCount(document.Records.Count);
    }

    [Fact]
    public void A_report_for_another_commit_is_refused()
    {
        var json = DependencyTestKit.ContractFixture("valid", "lookup.npm.json");

        var parse = LookupReportParser.Parse(json, new string('a', 40));

        parse.IsValid.Should().BeFalse();
        parse.Errors.Should().ContainSingle(e => e.Contains("R3"));
    }

    [Fact]
    public void A_broken_report_is_an_error_not_an_exception()
    {
        LookupReportParser.Parse("[]", DependencyTestKit.Sha).IsValid.Should().BeFalse();
        LookupReportParser.Parse(DependencyTestKit.ContractFixture("invalid", "lookup.unknown-update-type.json"), DependencyTestKit.Sha).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_candidate_without_an_updateType_is_held_and_its_neighbours_still_ship()
    {
        var lookup = DependencyTestKit.Lookup(
            DependencyTestKit.Record("left-pad", "1.0.0", DependencyTestKit.Candidate(null, "1.0.1")),
            DependencyTestKit.Record("chalk", "4.1.0", DependencyTestKit.Candidate(EUpdateType.Patch, "4.1.2")));

        var outcome = DependencyTestKit.Evaluate(lookup);

        DependencyTestKit.Planned(outcome).Should().Equal(["chalk: chalk@4.1.2"]);
        outcome.Held.Should().ContainSingle(e => e.DepName == "left-pad" && e.Reason == EDependencyReportReason.MissingField);
    }

    [Fact]
    public void An_unknown_manager_and_ecosystem_pass_through_untouched()
    {
        var record = DependencyTestKit.Record("rules_go", "0.50.1", DependencyTestKit.Candidate(EUpdateType.Patch, "0.50.2")) with
        {
            Ecosystem = "bazel",
            Manager = "bazel-module",
            Datasource = "bazel",
            PackageFile = "MODULE.bazel",
            LockFiles = [],
        };

        var outcome = DependencyTestKit.Evaluate(DependencyTestKit.Lookup(record));

        var item = outcome.Groups.Should().ContainSingle().Which.Items.Should().ContainSingle().Which.ToPlanItem();
        item.Ecosystem.Should().Be("bazel");
        item.Manager.Should().Be("bazel-module");
        item.NewValue.Should().Be("0.50.2");
    }

    [Fact]
    public void A_Go_pseudo_version_digest_is_never_read_as_a_version()
    {
        // S12: golang.org/x/exp v0.0.0-20230801… -> v0.0.0-20261014…, labelled digest. The floor sees 0 and 0, 0 and 0.
        var record = RenovateReportMapper.Map(DependencyTestKit.RenovateReport("go")).Single(r => r.DepName == "golang.org/x/exp");

        var outcome = DependencyTestKit.Evaluate(DependencyTestKit.Lookup(record), now: new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero));

        var group = outcome.Groups.Should().ContainSingle().Which;
        group.Level.Should().Be(EUpdateLevel.Patch);
        group.Items[0].ToPlanItem().UpdateType.Should().Be("digest");
    }

    [Fact]
    public void A_0x_minor_by_label_is_a_major_by_the_floor()
    {
        // S12: golang.org/x/text v0.3.7 -> v0.43.0 is labelled minor; under 0.x the second segment is the major (R17).
        var record = RenovateReportMapper.Map(DependencyTestKit.RenovateReport("go")).Single(r => r.DepName == "golang.org/x/text");
        record.Candidates.Single().UpdateType.Should().Be(EUpdateType.Minor);

        var outcome = DependencyTestKit.Evaluate(DependencyTestKit.Lookup(record), now: new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero));

        outcome.Groups.Should().BeEmpty();
        outcome.AwaitingApproval.Should().ContainSingle(e => e.DepName == "golang.org/x/text" && e.Level == EUpdateLevel.Major);
    }

    [Fact]
    public void A_Docker_digest_without_a_version_is_planned_at_the_current_version()
    {
        // S12: python:3.11-slim re-pushed under a new digest; Renovate gives no newVersion (D-P5).
        var record = RenovateReportMapper.Map(DependencyTestKit.RenovateReport("docker-actions-platform"))
            .Single(r => r.DepName == "python" && r.Manager == "dockerfile");
        record.Candidates.Should().Contain(c => c.UpdateType == EUpdateType.Digest && c.Version == null);

        var config = DependencyTestKit.Config("dependencies:\n  autoMerge:\n    default: patch\n");
        var outcome = DependencyTestKit.Evaluate(DependencyTestKit.Lookup(record), config, now: new DateTimeOffset(2026, 11, 30, 0, 0, 0, TimeSpan.Zero),
            state: Seen(record, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));

        var item = outcome.Groups.Should().ContainSingle().Which.Items.Should().ContainSingle().Which.ToPlanItem();
        item.NewValue.Should().Be("3.11-slim");
        item.NewVersion.Should().Be("3.11");
        item.NewDigest.Should().StartWith("sha256:e88e9");
    }

    [Theory]
    [InlineData("4.1.1", "4.2.0", EUpdateLevel.Minor, EUpdateLevel.Minor)]
    [InlineData("^4.17.0", "5.0.0", EUpdateLevel.Minor, EUpdateLevel.Major)]
    [InlineData("v4.1.1", "v5.0.0", EUpdateLevel.Patch, EUpdateLevel.Major)]
    [InlineData("0.3.7", "0.43.0", EUpdateLevel.Minor, EUpdateLevel.Major)]
    [InlineData("0.3.7", "0.3.9", EUpdateLevel.Patch, EUpdateLevel.Patch)]
    [InlineData("22.04", "resolute", EUpdateLevel.Minor, EUpdateLevel.Major)]
    [InlineData("2024.10.1", "2025.1.0", EUpdateLevel.Minor, EUpdateLevel.Major)]
    [InlineData("11.0.100-rc.1.26425.128", "11.0.100-rc.2.26504.105", EUpdateLevel.Patch, EUpdateLevel.Patch)]
    [InlineData("7.1.10-ubuntu.22.04-x64", "7.2.3-ubuntu.22.04-x64", EUpdateLevel.Minor, EUpdateLevel.Minor)]
    [InlineData("08.1", "8.2", EUpdateLevel.Minor, EUpdateLevel.Minor)]
    public void The_R17_floor_only_ever_raises_the_label(string current, string next, EUpdateLevel labeled, EUpdateLevel expected)
        => UpdateTypeFloor.Apply(labeled, current, next).Should().Be(expected);

    private static Entities.DependencyUpdateState Seen(LookupRecord record, DateTimeOffset at) => new()
    {
        Repository = "Repositories/github/1",
        FirstSeen = [.. record.Candidates.Select(c => new Entities.DependencySeenVersion
        {
            Ecosystem = record.Ecosystem,
            DepName = record.DepName,
            Version = DependencyPolicyEngine.Token(c),
            FirstSeenAtUtc = at.UtcDateTime,
        })],
    };
}
