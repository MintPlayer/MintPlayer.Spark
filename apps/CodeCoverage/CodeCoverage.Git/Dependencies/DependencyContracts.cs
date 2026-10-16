using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodeCoverage.Git.Dependencies;

/// <summary>
/// The C# side of the dependency-update contract, version 1 (<c>apps/CodeCoverage/contracts/dependency-update/*.schema.json</c>,
/// docs/code-coverage/dependency-updates-PRD.md §13.2, §13.11.2 "Contract test"). The runner (TS,
/// <c>dependency-action/src/contracts/types.ts</c>) writes <see cref="LookupDocument"/>, <see cref="ApplyResult"/> and
/// <see cref="MigrateResult"/>; the bot writes <see cref="DependencyPlan"/>.
/// </summary>
/// <remarks>
/// <para>
/// Plain DTOs on purpose: the schema is the authority on shape. The app validates every document against it before it
/// deserializes one (<c>CodeCoverage.Dependencies.DependencyContractReader</c>), so these types only need to round-trip.
/// They live here, in the assembly both the app and the git writer reference, so the writer's validators map from the
/// same plan the bot wrote.
/// </para>
/// <para>
/// A property the schema lists under <c>required</c> is always written, as <c>null</c> when it has no value. A property
/// the schema leaves optional carries <see cref="JsonIgnoreCondition.WhenWritingNull"/>, so the bot never writes a key the
/// runner did not expect.
/// </para>
/// </remarks>
public static class DependencyContract
{
    /// <summary>The <c>contractVersion</c> every document of this version carries.</summary>
    public const int Version = 1;

    public const string LookupKind = "lookup";
    public const string PlanKind = "plan";
    public const string ApplyResultKind = "apply-result";
    public const string PlatformKind = "platform";
    public const string MigrateResultKind = "migrate-result";

    /// <summary>Where the bot's plan commit puts the plan (R14).</summary>
    public const string PlanPath = ".mintplayer/dependency-plan.json";

    /// <summary>
    /// The serializer settings of the contract: camelCase, enums as their camelCase names, strict numbers, and unknown
    /// members refused (the schemas say <c>additionalProperties: false</c> everywhere).
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static string Serialize<T>(T document) => JsonSerializer.Serialize(document, JsonOptions);

    /// <summary>Deserializes without validating. Callers that read a runner's output validate against the schema first.</summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);
}

/// <summary>Renovate's <c>updateType</c> label, passed through by the runner (record.schema.json <c>$defs/updateType</c>).</summary>
/// <remarks>A label, not a fact: the bot raises it with the R17 floor (<c>UpdateTypeFloor</c>) before it uses it.</remarks>
public enum EUpdateType
{
    Major,
    Minor,
    Patch,
    Digest,
    Pin,
    PinDigest,
    LockfileUpdate,
    Rollback,
    Bump,
    Replacement,
}

/// <summary><c>lookup.json</c>: what the lookup workflow found (record.schema.json).</summary>
public sealed record LookupDocument
{
    public int ContractVersion { get; init; } = DependencyContract.Version;
    public string Kind { get; init; } = DependencyContract.LookupKind;
    public string ExpectedSha { get; init; } = string.Empty;
    public string GeneratedAt { get; init; } = string.Empty;
    public LookupEngine Engine { get; init; } = new();
    public List<LookupRecord> Records { get; init; } = [];
    public List<PlatformRecord> Platforms { get; init; } = [];
    public List<InterfaceDiff> InterfaceDiffs { get; init; } = [];
    public List<LookupProblem> Problems { get; init; } = [];
}

public sealed record LookupEngine
{
    public string Name { get; init; } = "renovate";

    /// <summary>The Renovate image, pinned by digest (R13).</summary>
    public string Image { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Seconds { get; init; }
}

public sealed record LookupProblem
{
    public string Source { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>One dependency occurrence: one depName in one package file (§13.2, R1).</summary>
public sealed record LookupRecord
{
    /// <summary>The adapter family derived from the datasource (<c>npm</c>, <c>nuget</c>, <c>platform:dotnet</c>, ...).</summary>
    public string Ecosystem { get; init; } = string.Empty;
    public string Manager { get; init; } = string.Empty;
    public string Datasource { get; init; } = string.Empty;
    public string PackageFile { get; init; } = string.Empty;
    public List<string> LockFiles { get; init; } = [];
    public string DepName { get; init; } = string.Empty;
    public string? PackageName { get; init; }
    public string? DepType { get; init; }
    public string? CurrentValue { get; init; }
    public string? CurrentVersion { get; init; }
    public string? LockedVersion { get; init; }
    public string? CurrentDigest { get; init; }
    public string? ReplaceString { get; init; }
    public string? SharedVariableName { get; init; }
    public string? GroupName { get; init; }
    public string? SkipReason { get; init; }

    /// <summary>The runner's R1 marker: some candidate lacks an <c>updateType</c>.</summary>
    public bool Hold { get; init; }

    public List<LookupCandidate> Candidates { get; init; } = [];
}

/// <summary>One update Renovate offers for a record.</summary>
public sealed record LookupCandidate
{
    /// <summary>Renovate's <c>newVersion</c>; may be a codename (ubuntu <c>resolute</c>). Never parsed beyond the R17 floor.</summary>
    public string? Version { get; init; }

    /// <summary>Renovate's <c>newValue</c>: the manifest token to write, range operator included.</summary>
    public string? Value { get; init; }

    public EUpdateType? UpdateType { get; init; }

    /// <summary>Null when unknown: the bot counts cooldown from its first-seen time (D-S12a).</summary>
    public string? ReleaseTimestamp { get; init; }

    /// <summary>Renovate's <c>newDigest</c>. For npm an integrity hash, not a version.</summary>
    public string? Digest { get; init; }

    public bool IsBreaking { get; init; }
    public bool IsLockfileUpdate { get; init; }
    public string? GroupName { get; init; }
    public string? BranchName { get; init; }

    /// <summary>Fields Renovate did not report: <c>version</c>, <c>value</c>, <c>updateType</c>, <c>releaseTimestamp</c>.</summary>
    public List<string> Missing { get; init; } = [];
}

/// <summary>R15: the <c>action.yml</c> interface diff of an Actions major against how the repository uses it.</summary>
public sealed record InterfaceDiff
{
    public string Action { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    public List<string> PackageFiles { get; init; } = [];
    public InterfaceRuntime Runtime { get; init; } = new();
    public List<string> AddedInputs { get; init; } = [];
    public List<string> RemovedInputs { get; init; } = [];
    public List<string> NewRequiredInputs { get; init; } = [];
    public List<string> RemovedOutputs { get; init; } = [];
    public List<InterfaceBlocker> Blockers { get; init; } = [];

    /// <summary><c>compatible</c>, <c>incompatible</c> or <c>unknown</c>.</summary>
    public string Verdict { get; init; } = "unknown";
}

public sealed record InterfaceRuntime
{
    public string? From { get; init; }
    public string? To { get; init; }
}

public sealed record InterfaceBlocker
{
    /// <summary><c>removed-input-used</c>, <c>new-required-input</c>, <c>missing-output-read</c> or <c>runtime-changed</c>.</summary>
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string File { get; init; } = string.Empty;
}

/// <summary>A platform/SDK record (platform-record.schema.json; R7, R8, D-S12c).</summary>
public sealed record PlatformRecord
{
    public int ContractVersion { get; init; } = DependencyContract.Version;
    public string Kind { get; init; } = DependencyContract.PlatformKind;

    /// <summary><c>dotnet</c>, <c>node</c>, <c>python</c>, <c>java</c> or <c>go</c>.</summary>
    public string Sdk { get; init; } = string.Empty;

    /// <summary><c>releases-index</c>, <c>endoflife.date</c> or <c>none</c>.</summary>
    public string Source { get; init; } = "none";

    public PlatformCurrent Current { get; init; } = new();
    public List<PlatformLocation> Locations { get; init; } = [];
    public List<PlatformCandidate> Candidates { get; init; } = [];
}

public sealed record PlatformCurrent
{
    /// <summary>Every channel found in an editable location, highest first.</summary>
    public List<string> Channels { get; init; } = [];

    /// <summary>A location already uses a preview: previews are followed only then (R7).</summary>
    public bool Preview { get; init; }
}

public sealed record PlatformLocation
{
    public string File { get; init; } = string.Empty;

    /// <summary><c>tfm</c>, <c>global.json</c>, <c>docker</c>, <c>setup-action</c>, ... (the schema's closed list).</summary>
    public string Kind { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;
    public string? Channel { get; init; }

    /// <summary>R8: a computed value (<c>$(Prop)</c>, a condition, a range) is reported, never edited.</summary>
    public bool Computed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

public sealed record PlatformCandidate
{
    public string Channel { get; init; } = string.Empty;
    public string? LatestVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LatestSdk { get; init; }

    public string? ReleaseTimestamp { get; init; }
    public bool Preview { get; init; }

    /// <summary>A date string, a boolean or null, as endoflife.date reports it.</summary>
    public JsonNode? Eol { get; init; }

    public bool? Lts { get; init; }
}

/// <summary><c>.mintplayer/dependency-plan.json</c> (plan.schema.json): what the apply run applies, group by group, in order.</summary>
public sealed record DependencyPlan
{
    public int ContractVersion { get; init; } = DependencyContract.Version;
    public string Kind { get; init; } = DependencyContract.PlanKind;
    public string PlanId { get; init; } = string.Empty;
    public string ExpectedSha { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanOptions? Options { get; init; }

    public List<PlanGroup> Groups { get; init; } = [];
}

public sealed record PlanOptions
{
    /// <summary>The workflow passes feed secrets; uv then runs with <c>--no-build</c> (D-S14b).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FeedSecrets { get; init; }

    /// <summary>The D-C3 whole-tree consistency check; the runner defaults it to on.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ConsistencyCheck { get; init; }
}

public sealed record PlanGroup
{
    public string Group { get; init; } = string.Empty;
    public List<PlanItem> Items { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanPlatform? Platform { get; init; }
}

/// <summary>One planned update. Every token comes from a lookup record: the runner writes <see cref="NewValue"/> where
/// <see cref="CurrentValue"/> was, and pins the lock at <see cref="NewVersion"/>.</summary>
public sealed record PlanItem
{
    public string DepName { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ecosystem { get; init; }

    public string Datasource { get; init; } = string.Empty;
    public string Manager { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DepType { get; init; }

    public string PackageFile { get; init; } = string.Empty;
    public List<string> LockFiles { get; init; } = [];
    public string CurrentValue { get; init; } = string.Empty;
    public string NewValue { get; init; } = string.Empty;
    public string NewVersion { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentDigest { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NewDigest { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReplaceString { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SharedVariableName { get; init; }

    public string UpdateType { get; init; } = string.Empty;
}

/// <summary>A platform group's instruction to the runner's SDK adapter (R7, R8).</summary>
public sealed record PlanPlatform
{
    public string Sdk { get; init; } = string.Empty;
    public string FromChannel { get; init; } = string.Empty;
    public string ToChannel { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SdkVersion { get; init; }

    /// <summary><c>replace</c> or <c>add</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Strategy { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KeepPrevious { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LockPackageMajor { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Locations { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Exclude { get; init; }
}

public enum EApplyGroupStatus
{
    /// <summary>In the patch.</summary>
    Applied,

    /// <summary>The resolver refused the set; the blockers say why and the bot holds the group (R2).</summary>
    Unresolvable,

    /// <summary>Anything else: no adapter, edit refused, tool missing, dropped by the consistency bisect.</summary>
    Failed,
}

public enum EConsistencyStatus
{
    Passed,
    Failed,
    Skipped,
}

/// <summary><c>result.json</c> (result.schema.json): why the groups the patch does not apply were not applied.</summary>
/// <remarks>Applied groups are derived from the PATCH, never from this file alone (gap A).</remarks>
public sealed record ApplyResult
{
    public int ContractVersion { get; init; } = DependencyContract.Version;
    public string Kind { get; init; } = DependencyContract.ApplyResultKind;
    public string PlanId { get; init; } = string.Empty;
    public string ExpectedSha { get; init; } = string.Empty;
    public List<ApplyGroupResult> Groups { get; init; } = [];
    public ApplyConsistency Consistency { get; init; } = new();
    public ArtifactPatch Patch { get; init; } = new();
}

public sealed record ApplyGroupResult
{
    public string Group { get; init; } = string.Empty;
    public EApplyGroupStatus Status { get; init; }
    public List<ApplyItemResult> Items { get; init; } = [];
    public List<string> Files { get; init; } = [];
    public List<ApplyBlocker> Blockers { get; init; } = [];
    public string? Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ApplyPlatformResult? Platform { get; init; }
}

public sealed record ApplyItemResult
{
    public string DepName { get; init; } = string.Empty;
    public string PackageFile { get; init; } = string.Empty;
    public string NewVersion { get; init; } = string.Empty;
    public string? Adapter { get; init; }
}

public sealed record ApplyBlocker
{
    /// <summary>The package whose version blocks; a newer version of it lifts the hold (R2). Null = unattributed.</summary>
    public string? DepName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }

    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed record ApplyPlatformResult
{
    public List<string> Edited { get; init; } = [];
    public List<ApplyPlatformReported> Reported { get; init; } = [];
}

public sealed record ApplyPlatformReported
{
    public string File { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed record ApplyConsistency
{
    public EConsistencyStatus Status { get; init; }
    public List<ApplyConsistencyCheck> Checks { get; init; } = [];

    /// <summary>Groups dropped by the D-C3 bisect.</summary>
    public List<string> Bisected { get; init; } = [];
}

public sealed record ApplyConsistencyCheck
{
    public string Adapter { get; init; } = string.Empty;
    public string Directory { get; init; } = string.Empty;
    public bool Ok { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}

/// <summary>The artifact's patch file, by name, size and hash.</summary>
public sealed record ArtifactPatch
{
    public string File { get; init; } = string.Empty;
    public long Bytes { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}

/// <summary><c>migrate-result.json</c> (migrate-result.schema.json): the completion marker of a migrate run (S17, R9, R19).</summary>
public sealed record MigrateResult
{
    public int ContractVersion { get; init; } = DependencyContract.Version;
    public string Kind { get; init; } = DependencyContract.MigrateResultKind;

    /// <summary><c>angular</c> or <c>nx</c>.</summary>
    public string Tool { get; init; } = string.Empty;

    public string BaseSha { get; init; } = string.Empty;
    public List<MigratePackage> Packages { get; init; } = [];

    /// <summary>Always true in a valid document: the marker is written only when every step exited 0.</summary>
    public bool Completed { get; init; }

    public List<MigrateStep> Steps { get; init; } = [];

    /// <summary>R19: dependency changes the migration made beyond the group.</summary>
    public List<MigrateExtraBump> ExtraBumps { get; init; } = [];

    public ArtifactPatch Patch { get; init; } = new();
}

public sealed record MigratePackage
{
    public string Name { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
}

public sealed record MigrateStep
{
    public string Name { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public double Seconds { get; init; }
}

public sealed record MigrateExtraBump
{
    public string DepName { get; init; } = string.Empty;
    public string PackageFile { get; init; } = string.Empty;
    public string? From { get; init; }
    public string? To { get; init; }
}
