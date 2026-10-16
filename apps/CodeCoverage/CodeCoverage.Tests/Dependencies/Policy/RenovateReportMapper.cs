using System.Text.Json.Nodes;
using CodeCoverage.Git.Dependencies;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// A line-for-line C# port of the runner's <c>mapReport()</c> (<c>dependency-action/src/lookup/mapReport.ts</c>): a Renovate
/// <c>report.json</c> to lookup records. Test-only, on purpose.
/// </summary>
/// <remarks>
/// The bot never reads a Renovate report: the lookup runner maps it and the bot reads only the contract's records (R1,
/// §13.2). The port exists so the policy tests can run on the REAL S12 reports, this repository's 504-dependency one
/// included, and <c>LookupReportParserTests</c> pins it to the runner: for the five samples the runner itself turned into
/// contract fixtures, both mappings must give the same records, or one side drifted.
/// </remarks>
public static class RenovateReportMapper
{
    private static readonly HashSet<string> UpdateTypes =
        ["major", "minor", "patch", "digest", "pin", "pinDigest", "lockfileUpdate", "rollback", "bump", "replacement"];

    private static readonly Dictionary<string, string> DatasourceEcosystem = new()
    {
        ["npm"] = "npm",
        ["nuget"] = "nuget",
        ["pypi"] = "pypi",
        ["maven"] = "maven",
        ["crate"] = "cargo",
        ["go"] = "go",
        ["packagist"] = "composer",
        ["docker"] = "docker",
        ["rubygems"] = "rubygems",
        ["dotnet-version"] = "platform:dotnet",
        ["node-version"] = "platform:node",
        ["python-version"] = "platform:python",
        ["java-version"] = "platform:java",
        ["golang-version"] = "platform:go",
        ["rust-version"] = "platform:rust",
        ["gradle-version"] = "gradle-wrapper",
        ["github-runners"] = "github-runners",
    };

    private static readonly Dictionary<string, string> PlatformDepNames = new()
    {
        ["node"] = "platform:node",
        ["nodejs"] = "platform:node",
        ["python"] = "platform:python",
        ["go"] = "platform:go",
        ["golang"] = "platform:go",
        ["java"] = "platform:java",
        ["java-jdk"] = "platform:java",
        ["dotnet-sdk"] = "platform:dotnet",
    };

    private static readonly Dictionary<string, string> PlatformManagers = new()
    {
        ["nvm"] = "platform:node",
        ["nodenv"] = "platform:node",
        ["pyenv"] = "platform:python",
    };

    public static string EcosystemOf(string manager, string datasource, string depName)
    {
        if (PlatformManagers.TryGetValue(manager, out var platform)) return platform;
        if (datasource.StartsWith("github-", StringComparison.Ordinal) && datasource != "github-runners")
        {
            if (manager == "github-actions" && depName.Contains('/')) return "github-actions";
            if (PlatformDepNames.TryGetValue(depName, out var byName)) return byName;
        }
        if (manager == "asdf" && PlatformDepNames.TryGetValue(depName, out var asdf)) return asdf;
        return DatasourceEcosystem.GetValueOrDefault(datasource) ?? datasource;
    }

    public static List<LookupRecord> Map(string reportJson)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var records = new List<LookupRecord>();
        foreach (var (_, repo) in report["repositories"]?.AsObject() ?? new JsonObject())
        {
            foreach (var (manager, files) in repo?["packageFiles"]?.AsObject() ?? new JsonObject())
            {
                foreach (var file in files!.AsArray())
                {
                    foreach (var dep in file!["deps"]?.AsArray() ?? new JsonArray())
                    {
                        var depName = Str(dep!["depName"]);
                        if (depName is null) continue;
                        var datasource = Str(dep["datasource"]) ?? Str(file["datasource"]) ?? "unknown";
                        var candidates = (dep["updates"]?.AsArray() ?? new JsonArray()).Select(u => Candidate(u!, dep)).ToList();
                        records.Add(new LookupRecord
                        {
                            Ecosystem = EcosystemOf(manager, datasource, depName),
                            Manager = manager,
                            Datasource = datasource,
                            PackageFile = Str(file["packageFile"])!,
                            LockFiles = [.. (file["lockFiles"]?.AsArray() ?? new JsonArray()).Select(l => l!.GetValue<string>())],
                            DepName = depName,
                            PackageName = Str(dep["packageName"]),
                            DepType = Str(dep["depType"]),
                            CurrentValue = Str(dep["currentValue"]),
                            CurrentVersion = Str(dep["currentVersion"]),
                            LockedVersion = Str(dep["lockedVersion"]),
                            CurrentDigest = Str(dep["currentDigest"]),
                            ReplaceString = Str(dep["replaceString"]),
                            SharedVariableName = Str(dep["sharedVariableName"]),
                            GroupName = Str(dep["groupName"]),
                            SkipReason = Str(dep["skipReason"]),
                            Hold = candidates.Any(c => c.UpdateType is null),
                            Candidates = candidates,
                        });
                    }
                }
            }
        }
        return records;
    }

    /// <summary>The records wrapped as a lookup document for <paramref name="expectedSha"/>.</summary>
    public static LookupDocument Document(string reportJson, string expectedSha, string generatedAt = "2026-10-15T19:17:00.000Z") => new()
    {
        ExpectedSha = expectedSha,
        GeneratedAt = generatedAt,
        Engine = new LookupEngine { Image = "renovate/renovate:44.125.2@sha256:a7dec3432d62b39872058424f0490829413b3f597f5d72fb41b9b7bd57ccac77" },
        Records = Map(reportJson),
    };

    private static LookupCandidate Candidate(JsonNode update, JsonNode dep)
    {
        var missing = new List<string>();
        var version = Str(update["newVersion"]);
        var value = Str(update["newValue"]);
        var type = Str(update["updateType"]);
        EUpdateType? updateType = type is not null && UpdateTypes.Contains(type) ? Enum.Parse<EUpdateType>(type, ignoreCase: true) : null;
        var releaseTimestamp = Str(update["releaseTimestamp"]);
        if (version is null) missing.Add("version");
        if (value is null && Str(update["newDigest"]) is null) missing.Add("value");
        if (updateType is null) missing.Add("updateType");
        if (releaseTimestamp is null) missing.Add("releaseTimestamp");
        return new LookupCandidate
        {
            Version = version,
            Value = value,
            UpdateType = updateType,
            ReleaseTimestamp = releaseTimestamp,
            Digest = Str(update["newDigest"]),
            IsBreaking = update["isBreaking"]?.GetValueKind() == System.Text.Json.JsonValueKind.True,
            IsLockfileUpdate = update["isLockfileUpdate"]?.GetValueKind() == System.Text.Json.JsonValueKind.True,
            GroupName = Str(update["groupName"]) ?? Str(dep["groupName"]),
            BranchName = Str(update["branchName"]),
            Missing = missing,
        };
    }

    /// <summary>The TS <c>str()</c>: a non-empty string, else null (a number or a boolean is not a string).</summary>
    private static string? Str(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String && value.GetValue<string>() is { Length: > 0 } s ? s : null;
}
