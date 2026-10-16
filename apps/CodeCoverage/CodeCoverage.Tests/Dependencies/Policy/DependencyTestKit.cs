using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.LookupReferences;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>Builders and file locations shared by the dependency policy tests.</summary>
public static class DependencyTestKit
{
    public const string Sha = "ee304c5c9ee2d72267b5092eb83f5546d8658baa";

    /// <summary>A fixed "now" for the synthetic cases; the replay uses the capture's own time.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    public static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MintPlayer.Spark.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"No repository root above '{AppContext.BaseDirectory}'.");
    }

    /// <summary>The shared contract directory (<c>apps/CodeCoverage/contracts/dependency-update</c>), read in place: one
    /// source for the runner's ajv and the bot's JsonSchema.Net.</summary>
    public static string ContractsDirectory() => Path.Combine(RepositoryRoot(), "apps", "CodeCoverage", "contracts", "dependency-update");

    public static string ContractFixture(string kind, string name) => File.ReadAllText(Path.Combine(ContractsDirectory(), "fixtures", kind, name));

    /// <summary>A real S12 Renovate report, copied into <c>Fixtures/Dependencies/renovate</c>.</summary>
    public static string RenovateReport(string sample)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Dependencies", "renovate", sample, "report.json"));

    public static LookupDocument Lookup(params LookupRecord[] records) => new()
    {
        ExpectedSha = Sha,
        GeneratedAt = "2026-10-16T12:00:00.000Z",
        Engine = new LookupEngine { Image = "renovate/renovate:44.125.2@sha256:a7dec3432d62b39872058424f0490829413b3f597f5d72fb41b9b7bd57ccac77" },
        Records = [.. records],
    };

    /// <summary>An npm dependency in <c>package.json</c> with a lockfile, pinned exactly at <paramref name="current"/>.</summary>
    public static LookupRecord Record(string depName, string current, params LookupCandidate[] candidates) => new()
    {
        Ecosystem = "npm",
        Manager = "npm",
        Datasource = "npm",
        PackageFile = "package.json",
        LockFiles = ["package-lock.json"],
        DepName = depName,
        PackageName = depName,
        DepType = "dependencies",
        CurrentValue = current,
        CurrentVersion = current,
        Hold = candidates.Any(c => c.UpdateType is null),
        Candidates = [.. candidates],
    };

    /// <summary>A candidate released long before <see cref="Now"/> unless <paramref name="released"/> says otherwise.</summary>
    public static LookupCandidate Candidate(EUpdateType? type, string version, string? released = "2026-09-01T00:00:00.000Z",
        string? value = null, string? group = null) => new()
    {
        Version = version,
        Value = value ?? version,
        UpdateType = type,
        ReleaseTimestamp = released,
        GroupName = group,
        Missing = Missing(type is null ? "updateType" : null, released is null ? "releaseTimestamp" : null),
    };

    private static List<string> Missing(params string?[] fields) => [.. fields.OfType<string>()];

    public static DependencyPolicyConfig Config(string? yml = null, params DependencyRule[] accountRules)
    {
        var parsed = DependenciesYml.Parse(yml);
        parsed.Errors.Should().BeEmpty();
        return new DependencyPolicyConfig { File = parsed.Section, AccountRules = accountRules };
    }

    public static DependencyRule AccountRule(string match, EDependencyThreshold maxUpdate, bool enforced = true, bool ignore = false, string? group = null)
        => new() { Id = match, Match = match, MaxUpdate = maxUpdate, Enforced = enforced, Ignore = ignore, Group = group };

    public static DependencyPolicyOutcome Evaluate(LookupDocument lookup, DependencyPolicyConfig? config = null, DependencyUpdateState? state = null,
        DependencyFleetState? fleet = null, DateTimeOffset? now = null, string configBlob = "", EDependencyRunMode mode = EDependencyRunMode.Scheduled,
        IReadOnlyCollection<DependencySecurityFix>? security = null, bool slot = true)
        => DependencyPolicyEngine.Evaluate(new DependencyPolicyInput
        {
            Lookup = lookup,
            Config = config ?? Config(),
            State = state ?? new DependencyUpdateState { Repository = "Repositories/github/1" },
            Fleet = fleet ?? new DependencyFleetState(),
            Now = now ?? Now,
            ConfigBlob = configBlob,
            Mode = mode,
            SecurityFixes = security ?? [],
            IsScheduledSlot = slot,
        });

    /// <summary>The planned groups as <c>group: dep@newValue, ...</c>, in plan order.</summary>
    public static IReadOnlyList<string> Planned(DependencyPolicyOutcome outcome)
        => [.. outcome.Groups.Select(g => $"{g.Group}: {string.Join(", ", g.Items.Select(i => $"{i.Record.DepName}@{i.ToPlanItem().NewValue}").Distinct())}")];
}
