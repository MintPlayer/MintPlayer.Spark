using System.Text.Json;
using System.Text.RegularExpressions;
using CodeCoverage.Git;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.Git.MergeQueue;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.GitWriter.Endpoints;

// The dependency updater's jobs (docs/code-coverage/dependency-updates-PRD.md R4, R14, R16). Same rules as the merge queue's
// endpoints: every value validated before it reaches git, a 400 naming the first refused field, a 200 with an outcome.

/// <summary><c>POST /jobs/dependencies/work-ref</c> (R14).</summary>
internal sealed partial class GitWriterDependencyWorkRefEndpoint : IPostEndpoint<DependencyWorkRefWire>
{
    public static string Path => "/jobs/dependencies/work-ref";

    [Inject] private readonly WriterService writer;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<DependencyWorkRefWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<DependencyWorkRefWire>(context.Request.Body, context.Request.ContentLength, DependencyFields.BodyBytes(options.Value, 1), context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(Refusal.Body(DependencyFields.BodyBytes(options.Value, 1)));

    public override async Task<IResult> HandleAsync(DependencyWorkRefWire request, CancellationToken cancellationToken)
    {
        var refusal = new Refusal();
        if (DependencyFields.Job(refusal, request, options.Value.AllowInsecureHttp) is not { } job
            || DependencyFields.Commit(refusal, request) is not { } commit
            || !refusal.Check(request.ExpectedWorkSha is null || WireValidation.ObjectId(request.ExpectedWorkSha) is not null, "expectedWorkSha", WireValidation.ShaRule)
            // Exactly one of the two: the plan commit, or the staged apply patch.
            || !refusal.Check(request.PlanJson is null != (request.Patch is null), "planJson", "either planJson (the plan commit) or patch (a migrate run's base), not both"))
            return refusal.Result();
        DependencyPatchInput? staged = null;
        if (request.PlanJson is { } planJson)
        {
            if (!refusal.Check(planJson.Length <= DependencyFields.MaxPlanBytes && DependencyFields.Plan(planJson, job.ExpectedSha) is not null, "planJson", DependencyFields.PlanRule))
                return refusal.Result();
        }
        else if (DependencyFields.PatchInput(refusal, request, job.ExpectedSha) is not { } input)
            return refusal.Result();
        else
            staged = input;
        return Results.Json(await writer.DependencyWorkRefAsync(job, commit, request.PlanJson, staged, request.ExpectedWorkSha), GitWriterHost.Json);
    }
}

/// <summary><c>POST /jobs/dependencies/validate</c>: the <c>dependencies</c> profile, nothing pushed.</summary>
internal sealed partial class GitWriterDependencyValidateEndpoint : IPostEndpoint<DependencyValidateWire>
{
    public static string Path => "/jobs/dependencies/validate";

    [Inject] private readonly WriterService writer;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<DependencyValidateWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<DependencyValidateWire>(context.Request.Body, context.Request.ContentLength, DependencyFields.BodyBytes(options.Value, 1), context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(Refusal.Body(DependencyFields.BodyBytes(options.Value, 1)));

    public override async Task<IResult> HandleAsync(DependencyValidateWire request, CancellationToken cancellationToken)
    {
        var refusal = new Refusal();
        if (DependencyFields.Job(refusal, request, options.Value.AllowInsecureHttp) is not { } job
            || DependencyFields.PatchInput(refusal, request, job.ExpectedSha) is not { } input)
            return refusal.Result();
        return Results.Json(await writer.DependencyValidateAsync(job, input), GitWriterHost.Json);
    }
}

/// <summary><c>POST /jobs/dependencies/rolling</c> (R4).</summary>
internal sealed partial class GitWriterDependencyRollingEndpoint : IPostEndpoint<DependencyRollingWire>
{
    public static string Path => "/jobs/dependencies/rolling";

    /// <summary>The most migration patches one rolling build takes (R9: one per approved group with a migrate rule).</summary>
    public const int MaxMigrations = 8;

    [Inject] private readonly WriterService writer;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<DependencyRollingWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<DependencyRollingWire>(context.Request.Body, context.Request.ContentLength, DependencyFields.BodyBytes(options.Value, MaxMigrations + 1), context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Refusal.Body(DependencyFields.BodyBytes(options.Value, MaxMigrations + 1)));

    public override async Task<IResult> HandleAsync(DependencyRollingWire request, CancellationToken cancellationToken)
    {
        var refusal = new Refusal();
        if (DependencyFields.Job(refusal, request, options.Value.AllowInsecureHttp) is not { } job
            || DependencyFields.Commit(refusal, request) is not { } commit
            || DependencyFields.PatchInput(refusal, request, job.ExpectedSha) is not { } input
            || !refusal.Check(request.ExpectedRollingSha is null || WireValidation.ObjectId(request.ExpectedRollingSha) is not null, "expectedRollingSha", WireValidation.ShaRule)
            || !refusal.Check(request.LastBotSha is null || WireValidation.ObjectId(request.LastBotSha) is not null, "lastBotSha", WireValidation.ShaRule)
            || refusal.Check(Migrations(request.Migrations), "migrations", $"at most {MaxMigrations} entries, each a group name and a base64 patch") is not { } migrations)
            return refusal.Result();
        return Results.Json(await writer.DependencyRollingAsync(job, commit, input, migrations, request.ExpectedRollingSha, request.LastBotSha), GitWriterHost.Json);
    }

    private static List<(string Group, byte[] Patch)>? Migrations(List<DependencyMigratePatchWire>? wires)
    {
        if (wires is null)
            return [];
        if (wires.Count > MaxMigrations)
            return null;
        var list = new List<(string, byte[])>();
        foreach (var wire in wires)
        {
            if (wire is null || !DependencyFields.IsGroup(wire.Group) || DependencyFields.Base64(wire.Patch) is not { } bytes)
                return null;
            list.Add((wire.Group!, bytes));
        }
        return list;
    }
}

/// <summary><c>POST /jobs/dependencies/files</c> (R16): the onboarding commit.</summary>
internal sealed partial class GitWriterDependencyFilesEndpoint : IPostEndpoint<DependencyFilesWire>
{
    public static string Path => "/jobs/dependencies/files";

    [Inject] private readonly WriterService writer;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<DependencyFilesWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<DependencyFilesWire>(context.Request.Body, context.Request.ContentLength, 2 * 1024 * 1024, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(Refusal.Body(2 * 1024 * 1024));

    public override async Task<IResult> HandleAsync(DependencyFilesWire request, CancellationToken cancellationToken)
    {
        var refusal = new Refusal();
        if (DependencyFields.Job(refusal, request, options.Value.AllowInsecureHttp) is not { } job
            || DependencyFields.Commit(refusal, request) is not { } commit
            || refusal.Check(Files(request.Files), "files", DependencyFields.OnboardingFilesRule) is not { } files)
            return refusal.Result();
        return Results.Json(await writer.DependencyFilesAsync(job, commit, files), GitWriterHost.Json);
    }

    private static List<(string Path, string? Content)>? Files(List<DependencyFileWire>? wires)
    {
        if (wires is not { Count: > 0 and <= 16 })
            return null;
        var list = new List<(string, string?)>();
        foreach (var wire in wires)
        {
            if (wire?.Path is not { } path || !DependencyFields.IsOnboardingPath(path) || list.Any(f => f.Item1 == path))
                return null;
            if (wire.Delete != (wire.Content is null) || wire.Content is { Length: > 256 * 1024 })
                return null;
            list.Add((path, wire.Content));
        }
        return list;
    }
}

/// <summary><c>DELETE /jobs/dependencies/work-ref</c> (R14).</summary>
internal sealed partial class GitWriterDependencyDeleteWorkRefEndpoint : IDeleteEndpoint<DependencyDeleteRefWire, DependencyJobResponse>
{
    public static string Path => "/jobs/dependencies/work-ref";

    [Inject] private readonly WriterService writer;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<DependencyDeleteRefWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<DependencyDeleteRefWire>(context.Request.Body, context.Request.ContentLength, Body.SmallBytes, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(Refusal.Body(Body.SmallBytes));

    public override async Task<IResult> HandleAsync(DependencyDeleteRefWire request, CancellationToken cancellationToken)
    {
        var refusal = new Refusal();
        if (!refusal.Check(WireValidation.IsRepo(request.Provider, request.RepoId), "repoId", WireValidation.RepoRule)
            || refusal.Check(WireValidation.RemoteUrl(request.CloneUrl, options.Value.AllowInsecureHttp), "cloneUrl", WireValidation.RemoteUrlRule) is not { } cloneUrl
            || refusal.Check(WireValidation.Credential(request.Credential), "credential", WireValidation.CredentialRule) is not { } credential
            || !refusal.Check(request.ExpectedSha is null || WireValidation.ObjectId(request.ExpectedSha) is not null, "expectedSha", WireValidation.ShaRule))
            return refusal.Result();
        return Results.Json(await writer.DependencyDeleteWorkRefAsync($"{request.Provider}/{request.RepoId}", cloneUrl, credential, request.ExpectedSha), GitWriterHost.Json);
    }
}

/// <summary>The fields the dependency jobs share, validated once.</summary>
internal static partial class DependencyFields
{
    public const int MaxPlanBytes = 4 * 1024 * 1024;
    public const int MaxGroups = 500;
    public const int MaxTargetActions = 200;
    public const int MaxActionBytes = 1024 * 1024;
    public const int MaxHosts = 50;

    public const string PlanRule = "plan.schema.json as JSON text of at most 4 MiB, with the request's expectedSha";
    public const string OnboardingFilesRule = "1 to 16 distinct files, each a path of the onboarding set and either content (at most 256 KiB) or delete";

    /// <summary>A body with <paramref name="patches"/> patches at the writer's cap, plus the plan and the rest.</summary>
    public static long BodyBytes(GitWriterOptions options, int patches) => Body.PatchBytes(options.MaxPatchBytes) * patches + MaxPlanBytes + 4L * MaxActionBytes;

    /// <summary>The repository, the credential, the default branch and <c>expected_sha</c>.</summary>
    public static DependencyJob? Job(Refusal refusal, DependencyJobWire request, bool allowInsecureHttp)
    {
        if (!refusal.Check(WireValidation.IsRepo(request.Provider, request.RepoId), "repoId", WireValidation.RepoRule)
            || refusal.Check(WireValidation.RemoteUrl(request.CloneUrl, allowInsecureHttp), "cloneUrl", WireValidation.RemoteUrlRule) is not { } cloneUrl
            || refusal.Check(WireValidation.Credential(request.Credential), "credential", WireValidation.CredentialRule) is not { } credential
            || refusal.Check(WireValidation.Branch(request.BaseBranch), "baseBranch", MergeQueueWireRules.BranchRule) is not { } baseBranch
            || refusal.Check(WireValidation.ObjectId(request.ExpectedSha), "expectedSha", WireValidation.ShaRule) is not { } expectedSha)
            return null;
        return new DependencyJob($"{request.Provider}/{request.RepoId}", cloneUrl, credential, baseBranch, expectedSha);
    }

    public static DependencyCommit? Commit(Refusal refusal, DependencyCommitJobWire request)
    {
        if (refusal.Check(WireValidation.DatedIdentity(request.Author), "author", WireValidation.DatedIdentityRule) is not { } author
            || refusal.Check(WireValidation.DatedIdentity(request.Committer), "committer", WireValidation.DatedIdentityRule) is not { } committer
            || refusal.Check(WireValidation.Message(request.Message), "message", MergeQueueWireRules.MessageRule) is not { } message)
            return null;
        return new DependencyCommit(author, committer, message);
    }

    public static DependencyPatchInput? PatchInput(Refusal refusal, DependencyPatchJobWire request, string expectedSha)
    {
        if (refusal.Check(request.Plan is { Length: <= MaxPlanBytes } text ? Plan(text, expectedSha) : null, "plan", PlanRule) is not { } plan
            || refusal.Check(Base64(request.Patch), "patch", "base64, or empty for a run that applied nothing") is not { } patch
            || !refusal.Check(request.ClaimedGroups is null || request.ClaimedGroups.Count <= MaxGroups && request.ClaimedGroups.All(IsGroup), "claimedGroups", $"at most {MaxGroups} group names")
            || !refusal.Check(request.TargetActions is null || request.TargetActions.Count <= MaxTargetActions
                && request.TargetActions.All(a => a.Key.Length is > 0 and <= 512 && a.Value.Length <= MaxActionBytes), "targetActions", $"at most {MaxTargetActions} action.yml texts of at most 1 MiB")
            || !refusal.Check(request.AllowedHosts is null || request.AllowedHosts.Count <= MaxHosts && request.AllowedHosts.All(h => Host().IsMatch(h)), "allowedHosts", $"at most {MaxHosts} host names"))
            return null;
        return new DependencyPatchInput(plan, patch, request.ClaimedGroups, request.TargetActions, request.AllowedHosts ?? []);
    }

    /// <summary>The plan, when it deserializes and names <paramref name="expectedSha"/>; the app validated it against the schema.</summary>
    public static DependencyPlan? Plan(string json, string expectedSha)
    {
        try
        {
            var plan = DependencyContract.Deserialize<DependencyPlan>(json);
            return plan is not null && plan.ExpectedSha == expectedSha && plan.Kind == DependencyContract.PlanKind && plan.Groups.All(g => IsGroup(g.Group)) ? plan : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsGroup(string? value) => value is { Length: > 0 and <= 200 } && !value.Any(char.IsControl);

    public static byte[]? Base64(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return [];
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>R16's closed set: the four templates, <c>coverage.yml</c> and Dependabot's file. No other path is ever written.</summary>
    public static bool IsOnboardingPath(string path) => path is ".github/workflows/dependency-lookup.yml" or ".github/workflows/dependency-apply.yml"
        or ".github/workflows/angular-migrate.yml" or ".github/workflows/nx-migrate.yml" or "coverage.yml" or ".github/dependabot.yml" or ".github/dependabot.yaml";

    [GeneratedRegex(@"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Host();
}
