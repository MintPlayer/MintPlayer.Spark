using CodeCoverage.Git;
using CodeCoverage.Git.MergeQueue;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Validation;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.GitWriter.Endpoints;

/// <summary><c>POST /jobs/versioning/run</c>: one server-host versioning run (#153 M6).</summary>
internal sealed partial class GitWriterVersioningRunEndpoint : IPostEndpoint<VersioningRunRequestWire>
{
    public const int MaxIgnore = 64;
    public const int MaxGroups = 64;
    public const int MaxAnchorLength = 256;
    public const string BranchMajorRule = "null, or a major between 0 and 999999";
    public const string ManifestRule = "a release manifest: version 1, a commit id of 40 lowercase hex characters and well-formed packages";
    public static readonly string SettingsRule = $"at most {MaxIgnore} ignore globs, {MaxGroups} named groups of package patterns and "
        + $"{Limits.MaxPackagesPerRun} anchors by package id, every glob at most {Limits.MaxGlobLength} characters";

    public static string Path => "/jobs/versioning/run";

    [Inject] private readonly VersioningRunService runs;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<VersioningRunRequestWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<VersioningRunRequestWire>(context.Request.Body, context.Request.ContentLength, Limits.MaxManifestBytes, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(Refusal.Body(Limits.MaxManifestBytes));

    public override async Task<IResult> HandleAsync(VersioningRunRequestWire request, CancellationToken cancellationToken)
    {
        var refusal = new Refusal();
        if (!refusal.Check(WireValidation.IsRepo(request.Provider, request.RepoId), "repoId", WireValidation.RepoRule)
            || refusal.Check(WireValidation.RemoteUrl(request.CloneUrl, options.Value.AllowInsecureHttp), "cloneUrl", WireValidation.RemoteUrlRule) is not { } cloneUrl
            || refusal.Check(WireValidation.Credential(request.Credential), "credential", WireValidation.CredentialRule) is not { } credential
            || refusal.Check(WireValidation.Branch(request.Branch), "branch", MergeQueueWireRules.BranchRule) is not { } branch
            || refusal.Check(WireValidation.Branch(request.DefaultBranch), "defaultBranch", MergeQueueWireRules.BranchRule) is not { } defaultBranch
            || !refusal.Check(request.BranchMajor is null or (>= 0 and <= 999_999), "branchMajor", BranchMajorRule)
            || refusal.Check(Tagger(request.Tagger), "tagger", MergeQueueWireRules.IdentityRule) is not { } tagger
            || refusal.Check(Settings(request.Settings), "settings", SettingsRule) is not { } settings
            || refusal.Check(request.Manifest is { } manifest && manifest.Validate().Count == 0 ? manifest : null, "manifest", ManifestRule) is not { } valid)
            return refusal.Result();

        var run = new ReleaseRunRequest(valid, settings, branch == defaultBranch, request.BranchMajor);
        return Results.Json(await runs.RunAsync(new VersioningRunJob($"{request.Provider}/{request.RepoId}", cloneUrl, credential, branch, defaultBranch, tagger, run)),
            GitWriterHost.Json);
    }

    private static GitIdentity? Tagger(IdentityWire? wire)
        => wire is not null && MergeQueueWireRules.IsIdentity(wire.Name, wire.Email) ? new GitIdentity(wire.Name!, wire.Email!) : null;

    /// <summary>The engine's settings, every glob compiled once so a malformed one is a 400 here rather than a failed job.</summary>
    internal static VersioningSettings? Settings(VersioningSettingsWire? wire)
    {
        if (wire is null) return new VersioningSettings();
        var ignore = wire.Ignore ?? [.. VersioningSettings.DefaultIgnore];
        var groups = wire.Groups ?? [];
        var anchors = wire.Anchors ?? [];
        var releaseBranches = wire.ReleaseBranches is { Count: > 0 } patterns ? patterns : [.. ReleaseBranches.Default];
        if (ignore.Count > MaxIgnore || groups.Count > MaxGroups || anchors.Count > Limits.MaxPackagesPerRun) return null;
        if (releaseBranches.Count > VersioningConfig.MaxReleaseBranches || !releaseBranches.All(VersioningConfig.IsBranchPattern)) return null;
        if (!ignore.All(IsGlob)) return null;
        if (!groups.All(g => g is { Name.Length: > 0 and <= 100, Packages.Count: > 0 and <= Limits.MaxPackagesPerRun } && g.Packages.All(IsPattern))) return null;
        if (groups.Select(g => g.Name).Distinct(StringComparer.Ordinal).Count() != groups.Count) return null;
        if (!anchors.All(a => Identifiers.IsPackageId(a.Key) && a.Value is { Length: > 0 and <= MaxAnchorLength } && !a.Value.Any(char.IsControl))) return null;
        return new VersioningSettings
        {
            Ignore = ignore,
            Groups = groups.Select(g => new LockstepGroup(g.Name!, g.Packages!)).ToList(),
            Anchors = new Dictionary<string, string>(anchors, StringComparer.Ordinal),
            ReleaseBranches = releaseBranches,
        };
    }

    private static bool IsGlob(string? glob)
    {
        try
        {
            return glob is not null && new Versioning.Graph.PathGlob(glob) is not null;
        }
        catch (GraphException)
        {
            return false;
        }
    }

    /// <summary>A package pattern of a group (D13): a package id, with <c>*</c> and <c>?</c> standing in for any characters.</summary>
    private static bool IsPattern(string? pattern)
        => pattern is { Length: > 0 } && pattern.Length <= Limits.MaxGlobLength && !pattern.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));
}

/// <summary><c>POST /jobs/versioning/plan</c>: would a run release anything on this commit? (#153 M11, D3 trigger B)</summary>
internal sealed partial class GitWriterVersioningPlanEndpoint : IPostEndpoint<VersioningPlanRequestWire>
{
    public static string Path => "/jobs/versioning/plan";

    [Inject] private readonly VersioningRunService runs;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<VersioningPlanRequestWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<VersioningPlanRequestWire>(context.Request.Body, context.Request.ContentLength, Limits.MaxManifestBytes, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(Refusal.Body(Limits.MaxManifestBytes));

    public override async Task<IResult> HandleAsync(VersioningPlanRequestWire request, CancellationToken cancellationToken)
    {
        var refusal = new Refusal();
        if (!refusal.Check(WireValidation.IsRepo(request.Provider, request.RepoId), "repoId", WireValidation.RepoRule)
            || refusal.Check(WireValidation.RemoteUrl(request.CloneUrl, options.Value.AllowInsecureHttp), "cloneUrl", WireValidation.RemoteUrlRule) is not { } cloneUrl
            || refusal.Check(WireValidation.Credential(request.Credential), "credential", WireValidation.CredentialRule) is not { } credential
            || refusal.Check(WireValidation.Branch(request.Branch), "branch", MergeQueueWireRules.BranchRule) is not { } branch
            || refusal.Check(WireValidation.Branch(request.DefaultBranch), "defaultBranch", MergeQueueWireRules.BranchRule) is not { } defaultBranch
            || refusal.Check(WireValidation.ObjectId(request.Commit), "commit", WireValidation.ShaRule) is not { } commit
            || !refusal.Check(request.BranchMajor is null or (>= 0 and <= 999_999), "branchMajor", GitWriterVersioningRunEndpoint.BranchMajorRule)
            || refusal.Check(GitWriterVersioningRunEndpoint.Settings(request.Settings), "settings", GitWriterVersioningRunEndpoint.SettingsRule) is not { } settings)
            return refusal.Result();

        return Results.Json(await runs.PlanAsync(new VersioningPlanJob($"{request.Provider}/{request.RepoId}", cloneUrl, credential, branch, defaultBranch, commit,
            request.BranchMajor, settings)), GitWriterHost.Json);
    }
}

/// <summary><c>POST /jobs/versioning/onboarding</c>: the onboarding pull request's commit (#153 D17).</summary>
internal sealed partial class GitWriterVersioningOnboardingEndpoint : IPostEndpoint<VersioningOnboardingWire>
{
    public const int MaxFiles = 256;
    public const int MaxFileBytes = 256 * 1024;
    public const long MaxBodyBytes = 16L * 1024 * 1024;
    public static readonly string FilesRule = $"1 to {MaxFiles} distinct files of at most {MaxFileBytes / 1024} KiB text, no deletions; every path "
        + "printable ASCII without spaces, quotes, backslashes, empty, . or .. components, nothing under .git, no .gitmodules or .gitattributes, "
        + "and under .github only .github/workflows/*.yml or *.yaml";

    public static string Path => "/jobs/versioning/onboarding";

    [Inject] private readonly WriterService writer;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<VersioningOnboardingWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<VersioningOnboardingWire>(context.Request.Body, context.Request.ContentLength, MaxBodyBytes, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(Refusal.Body(MaxBodyBytes));

    public override async Task<IResult> HandleAsync(VersioningOnboardingWire request, CancellationToken cancellationToken)
    {
        var refusal = new Refusal();
        if (DependencyFields.Job(refusal, request, options.Value.AllowInsecureHttp) is not { } job
            || DependencyFields.Commit(refusal, request) is not { } commit
            || refusal.Check(Files(request.Files), "files", FilesRule) is not { } files)
            return refusal.Result();
        return Results.Json(await writer.VersioningOnboardingAsync(job, commit, files), GitWriterHost.Json);
    }

    internal static List<(string Path, string? Content)>? Files(List<DependencyFileWire>? wires)
    {
        if (wires is not { Count: > 0 and <= MaxFiles }) return null;
        var list = new List<(string Path, string? Content)>();
        foreach (var wire in wires)
        {
            if (wire is not { Path: { } path, Content: { Length: <= MaxFileBytes } content, Delete: false } || !IsPath(path)
                || list.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                return null;
            list.Add((path, content));
        }
        return list;
    }

    /// <summary>The patch validator's writable path; under <c>.github</c> only a workflow file, which the token's
    /// <c>workflows</c> permission covers.</summary>
    internal static bool IsPath(string path)
        => PatchValidator.IsWritablePath(path)
            && (!PatchValidator.IsUnderGitHub(path)
                || path.StartsWith(".github/workflows/", StringComparison.Ordinal) && path.Count(c => c == '/') == 2
                    && (path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal)));
}

/// <summary><c>POST /jobs/versioning/backport</c>: apply a merged pull request to release branches (#153 M10, D10).</summary>
internal sealed partial class GitWriterVersioningBackportEndpoint : IPostEndpoint<VersioningBackportRequestWire>
{
    public const int MaxTargets = 10;
    public const int MaxSubjects = 250;
    public const int MaxTitle = 256;
    public const string HeadPrefix = "backport/";
    public const string PullRequestRule = "a pull request number above 0";
    public static readonly string TitleRule = $"1 to {MaxTitle} characters, no control characters";
    public static readonly string SubjectsRule = $"at most {MaxSubjects} commit subjects of 1 to {MaxTitle} characters, no control characters";
    public static readonly string TargetsRule = $"1 to {MaxTargets} targets with distinct branches and heads, none the source branch, every head a branch under {HeadPrefix}";

    public static string Path => "/jobs/versioning/backport";

    [Inject] private readonly BackportService backports;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<VersioningBackportRequestWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<VersioningBackportRequestWire>(context.Request.Body, context.Request.ContentLength, Body.SmallBytes, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(Refusal.Body(Body.SmallBytes));

    public override async Task<IResult> HandleAsync(VersioningBackportRequestWire request, CancellationToken cancellationToken)
    {
        var refusal = new Refusal();
        if (!refusal.Check(WireValidation.IsRepo(request.Provider, request.RepoId), "repoId", WireValidation.RepoRule)
            || refusal.Check(WireValidation.RemoteUrl(request.CloneUrl, options.Value.AllowInsecureHttp), "cloneUrl", WireValidation.RemoteUrlRule) is not { } cloneUrl
            || refusal.Check(WireValidation.Credential(request.Credential), "credential", WireValidation.CredentialRule) is not { } credential
            || refusal.Check(WireValidation.Branch(request.SourceBranch), "sourceBranch", MergeQueueWireRules.BranchRule) is not { } source
            || refusal.Check(WireValidation.ObjectId(request.Commit), "commit", WireValidation.ShaRule) is not { } commit
            || !refusal.Check(request.PullRequest > 0, "pullRequest", PullRequestRule)
            || refusal.Check(IsLine(request.Title) ? request.Title : null, "title", TitleRule) is not { } title
            || refusal.Check(request.Subjects is null or { Count: <= MaxSubjects } && (request.Subjects ?? []).All(IsLine) ? request.Subjects ?? [] : null, "subjects", SubjectsRule) is not { } subjects
            || refusal.Check(Committer(request.Committer), "committer", MergeQueueWireRules.IdentityRule) is not { } committer
            || refusal.Check(Targets(request.Targets, source), "targets", TargetsRule) is not { } targets)
            return refusal.Result();

        return Results.Json(await backports.RunAsync(new BackportJob($"{request.Provider}/{request.RepoId}", cloneUrl, credential, source, commit, request.PullRequest,
            title, subjects, committer, targets)), GitWriterHost.Json);
    }

    private static bool IsLine(string? value) => value is { Length: > 0 and <= MaxTitle } && !value.Any(char.IsControl);

    private static GitIdentity? Committer(IdentityWire? wire)
        => wire is not null && MergeQueueWireRules.IsIdentity(wire.Name, wire.Email) ? new GitIdentity(wire.Name!, wire.Email!) : null;

    internal static List<(string Branch, string Head)>? Targets(List<BackportTargetWire>? wires, string source)
    {
        if (wires is not { Count: > 0 and <= MaxTargets }) return null;
        var targets = new List<(string Branch, string Head)>();
        foreach (var wire in wires)
        {
            if (WireValidation.Branch(wire.Branch) is not { } branch || WireValidation.Branch(wire.Head) is not { } head) return null;
            if (branch == source || !head.StartsWith(HeadPrefix, StringComparison.Ordinal) || head.Length == HeadPrefix.Length) return null;
            targets.Add((branch, head));
        }
        var distinct = targets.Select(t => t.Branch).Concat(targets.Select(t => t.Head)).Distinct(StringComparer.Ordinal).Count() == targets.Count * 2;
        return distinct ? targets : null;
    }
}
