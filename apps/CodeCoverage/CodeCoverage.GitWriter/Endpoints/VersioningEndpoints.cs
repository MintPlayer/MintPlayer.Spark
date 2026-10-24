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
        if (ignore.Count > MaxIgnore || groups.Count > MaxGroups || anchors.Count > Limits.MaxPackagesPerRun) return null;
        if (!ignore.All(IsGlob)) return null;
        if (!groups.All(g => g is { Name.Length: > 0 and <= 100, Packages.Count: > 0 and <= Limits.MaxPackagesPerRun } && g.Packages.All(IsPattern))) return null;
        if (groups.Select(g => g.Name).Distinct(StringComparer.Ordinal).Count() != groups.Count) return null;
        if (!anchors.All(a => Identifiers.IsPackageId(a.Key) && a.Value is { Length: > 0 and <= MaxAnchorLength } && !a.Value.Any(char.IsControl))) return null;
        return new VersioningSettings
        {
            Ignore = ignore,
            Groups = groups.Select(g => new LockstepGroup(g.Name!, g.Packages!)).ToList(),
            Anchors = new Dictionary<string, string>(anchors, StringComparer.Ordinal),
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
