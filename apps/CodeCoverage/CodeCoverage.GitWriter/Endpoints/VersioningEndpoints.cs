using CodeCoverage.Git;
using CodeCoverage.Git.MergeQueue;
using CodeCoverage.GitWriter.Jobs;
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
