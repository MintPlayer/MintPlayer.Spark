using System.Text.Json;
using CodeCoverage.Git.MergeQueue;
using CodeCoverage.GitWriter.Jobs;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.GitWriter.Endpoints;

// The writer's internal API. Reachable only on the coverage-git-writer network; every value is validated before it reaches
// a path, an argument or the environment, and a refusal is a 400 naming the first field that broke its rule (§0 D57).
// Every job's answer is 200 with an outcome.

/// <summary><c>GET /healthz</c>: always 200 <c>ok</c> (the compose healthcheck).</summary>
internal sealed class GitWriterHealthEndpoint : IGetEndpoint
{
    public static string Path => "/healthz";

    public Task<IResult> HandleAsync(HttpContext httpContext) => Task.FromResult(Results.Text("ok"));
}

/// <summary><c>POST /jobs/prepare</c>: §5.1 step [1].</summary>
internal sealed partial class GitWriterPrepareEndpoint : IPostEndpoint<PrepareRequestWire>
{
    public static string Path => "/jobs/prepare";

    [Inject] private readonly WriterService writer;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<PrepareRequestWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<PrepareRequestWire>(context.Request.Body, context.Request.ContentLength, Body.SmallBytes, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(Refusal.Body(Body.SmallBytes));

    public override async Task<IResult> HandleAsync(PrepareRequestWire request, CancellationToken cancellationToken)
    {
        var refusal = new Refusal();
        if (PrepareFields.Validate(refusal, request, options.Value.AllowInsecureHttp, baseShaRequired: false) is not { } job)
            return refusal.Result();
        return Results.Json(await writer.PrepareAsync(job), GitWriterHost.Json);
    }
}

/// <summary>
/// <c>POST /jobs/queue-branch</c> (§0 D65): rebuilds the prepared commit and pushes it to <c>mq/…</c>, which the saga asks
/// for only before a dispatch.
/// </summary>
internal sealed partial class GitWriterQueueBranchPushEndpoint : IPostEndpoint<QueueBranchPushWire>
{
    public static string Path => "/jobs/queue-branch";

    [Inject] private readonly WriterService writer;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<QueueBranchPushWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<QueueBranchPushWire>(context.Request.Body, context.Request.ContentLength, Body.SmallBytes, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(Refusal.Body(Body.SmallBytes));

    public override async Task<IResult> HandleAsync(QueueBranchPushWire request, CancellationToken cancellationToken)
    {
        var refusal = new Refusal();
        if (PrepareFields.Validate(refusal, request, options.Value.AllowInsecureHttp, baseShaRequired: true) is not { } prepare
            || refusal.Check(WireValidation.ObjectId(request.PreparedSha), "preparedSha", WireValidation.ShaRule) is not { } preparedSha
            || refusal.Check(WireValidation.QueueBranch(request.QueueBranch), "queueBranch", WireValidation.QueueBranchRule) is not { } queueBranch
            || !refusal.Check(request.ExpectedQueueSha is null || WireValidation.ObjectId(request.ExpectedQueueSha) is not null, "expectedQueueSha", WireValidation.ShaRule)
            // O25, §0 D67: whether a fork's .github change may go is never assumed.
            || !refusal.Check(request.HeadFromFork is not null, "headFromFork", "true or false"))
            return refusal.Result();
        return Results.Json(await writer.PushQueueBranchAsync(new QueueBranchJob(prepare, preparedSha, queueBranch, request.ExpectedQueueSha, request.HeadFromFork!.Value)),
            GitWriterHost.Json);
    }
}

/// <summary><c>POST /jobs/publish</c>: §5.1 step [3].</summary>
internal sealed partial class GitWriterPublishEndpoint : IPostEndpoint<PublishRequestWire>
{
    public static string Path => "/jobs/publish";

    [Inject] private readonly WriterService writer;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<PublishRequestWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<PublishRequestWire>(context.Request.Body, context.Request.ContentLength, Body.PatchBytes(options.Value.MaxPatchBytes),
            context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Refusal.Body(Body.PatchBytes(options.Value.MaxPatchBytes)));

    public override async Task<IResult> HandleAsync(PublishRequestWire request, CancellationToken cancellationToken)
    {
        var insecure = options.Value.AllowInsecureHttp;
        var refusal = new Refusal();
        if (PrepareFields.Validate(refusal, request, insecure, baseShaRequired: true) is not { } prepare
            // A fork's branch is pushed to the fork (O3); otherwise to the base repository.
            || refusal.Check(request.PushUrl is null ? prepare.CloneUrl : WireValidation.RemoteUrl(request.PushUrl, insecure), "pushUrl", WireValidation.RemoteUrlRule) is not { } pushUrl
            || refusal.Check(WireValidation.ObjectId(request.PreparedSha), "preparedSha", WireValidation.ShaRule) is not { } preparedSha
            || refusal.Check(WireValidation.Branch(request.PrBranch), "prBranch", MergeQueueWireRules.BranchRule) is not { } prBranch
            || refusal.Check(WireValidation.Globs(request.RegeneratedPaths), "regeneratedPaths", WireValidation.RegeneratedPathsRule) is not { } globs
            || refusal.Check(Patch(request.Patch), "patch", "base64, or null for no patch") is not { } patch)
            return refusal.Result();

        return Results.Json(await writer.PublishAsync(new PublishJob(prepare, pushUrl, preparedSha, prBranch, patch, globs)), GitWriterHost.Json);
    }

    /// <summary>The patch's bytes: empty for none, null when it is not base64.</summary>
    private static byte[]? Patch(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
            return [];
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary><c>DELETE /jobs/queue-branch</c>: removes <c>mq/…</c> after its entry ended (D5). The body names it.</summary>
internal sealed partial class GitWriterDeleteQueueBranchEndpoint : IDeleteEndpoint<DeleteQueueBranchWire, DeleteQueueBranchResponse>
{
    public static string Path => "/jobs/queue-branch";

    [Inject] private readonly WriterService writer;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<DeleteQueueBranchWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<DeleteQueueBranchWire>(context.Request.Body, context.Request.ContentLength, Body.SmallBytes, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(Refusal.Body(Body.SmallBytes));

    public override async Task<IResult> HandleAsync(DeleteQueueBranchWire request, CancellationToken cancellationToken)
    {
        var refusal = new Refusal();
        if (!refusal.Check(WireValidation.IsRepo(request.Provider, request.RepoId), "repoId", WireValidation.RepoRule)
            || refusal.Check(WireValidation.RemoteUrl(request.CloneUrl, options.Value.AllowInsecureHttp), "cloneUrl", WireValidation.RemoteUrlRule) is not { } cloneUrl
            || refusal.Check(WireValidation.Credential(request.Credential), "credential", WireValidation.CredentialRule) is not { } credential
            || refusal.Check(WireValidation.QueueBranch(request.QueueBranch), "queueBranch", WireValidation.QueueBranchRule) is not { } queueBranch
            || !refusal.Check(request.ExpectedSha is null || WireValidation.ObjectId(request.ExpectedSha) is not null, "expectedSha", WireValidation.ShaRule))
            return refusal.Result();
        var job = new DeleteQueueBranchJob($"{request.Provider}/{request.RepoId}", cloneUrl, credential, queueBranch, request.ExpectedSha);
        return Results.Json(await writer.DeleteQueueBranchAsync(job), GitWriterHost.Json);
    }
}

/// <summary>
/// The fields every job that builds the prepared commit carries (§0 D65): the prepare's own, which the queue-branch push
/// and the publish repeat so that they rebuild the same commit instead of reading it from the queue branch.
/// </summary>
internal static class PrepareFields
{
    /// <summary>The validated job, or null after recording the first field that broke its rule.</summary>
    /// <param name="baseShaRequired">The queue-branch push and the publish build on the prepare's base, so they need it.</param>
    public static PrepareJob? Validate(Refusal refusal, PrepareRequestWire request, bool allowInsecureHttp, bool baseShaRequired)
    {
        if (!refusal.Check(WireValidation.IsRepo(request.Provider, request.RepoId), "repoId", WireValidation.RepoRule)
            || refusal.Check(WireValidation.RemoteUrl(request.CloneUrl, allowInsecureHttp), "cloneUrl", WireValidation.RemoteUrlRule) is not { } cloneUrl
            || refusal.Check(WireValidation.Credential(request.Credential), "credential", WireValidation.CredentialRule) is not { } credential
            || refusal.Check(WireValidation.Branch(request.BaseBranch), "baseBranch", MergeQueueWireRules.BranchRule) is not { } baseBranch
            || !refusal.Check(request.BaseSha is null ? !baseShaRequired : WireValidation.ObjectId(request.BaseSha) is not null, "baseSha", WireValidation.ShaRule)
            || refusal.Check(WireValidation.HeadRef(request.HeadRef), "headRef", WireValidation.HeadRefRule) is not { } headRef
            || refusal.Check(WireValidation.ObjectId(request.HeadSha), "headSha", WireValidation.ShaRule) is not { } headSha
            || refusal.Check(WireValidation.Rules(request.AutoResolve), "autoResolve", WireValidation.AutoResolveEntriesRule) is not { } rules
            || refusal.Check(WireValidation.DatedIdentity(request.Author), "author", WireValidation.DatedIdentityRule) is not { } author
            || refusal.Check(WireValidation.DatedIdentity(request.Committer), "committer", WireValidation.DatedIdentityRule) is not { } committer
            || refusal.Check(WireValidation.Message(request.Message), "message", MergeQueueWireRules.MessageRule) is not { } message)
            return null;
        return new PrepareJob($"{request.Provider}/{request.RepoId}", cloneUrl, credential, baseBranch, request.BaseSha, headRef, headSha,
            rules, author, committer, message);
    }
}

/// <summary>
/// The first field of a request that broke its rule, answered as a 400 (§0 D57). The saga ends the entry at once with it:
/// the same request would be refused again. Only the field's name and its rule are sent, never a value, because the
/// credential is one of them.
/// </summary>
internal sealed class Refusal
{
    private string? field;
    private string? rule;

    /// <summary><paramref name="value"/>, or null after recording <paramref name="name"/> as the refused field.</summary>
    public T? Check<T>(T? value, string name, string because) where T : class
    {
        if (value is null) Record(name, because);
        return value;
    }

    public bool Check(bool holds, string name, string because)
    {
        if (!holds) Record(name, because);
        return holds;
    }

    private void Record(string name, string because)
    {
        if (field is not null) return;
        field = name;
        rule = because;
    }

    public IResult Result() => Answer(field ?? "request", rule ?? "a valid request");

    /// <summary>A body that is too large, not JSON, or JSON <c>null</c>.</summary>
    public static IResult Body(long maxBytes) => Answer("body", $"a JSON object of at most {maxBytes} bytes");

    private static IResult Answer(string field, string rule)
        => Results.Json(new RefusedResponse(JobOutcomes.Refused, field, rule), GitWriterHost.Json, statusCode: StatusCodes.Status400BadRequest);
}

/// <summary>
/// Request bodies, read under a byte cap; anything too large, malformed or JSON <c>null</c> is null (a 400). Each endpoint's
/// <c>BindRequestAsync</c> hands in its request's stream, like the mirror's and the SEO fetcher's endpoints: the request is
/// read only in the typed-binding hook, and this helper never sees the <c>HttpContext</c> (§0 D80).
/// </summary>
internal static class Body
{
    public const int SmallBytes = 256 * 1024;

    /// <summary>A publish body: the base64 patch (4/3 of its bytes) plus the rest of the request.</summary>
    public static long PatchBytes(long maxPatchBytes) => maxPatchBytes / 3 * 4 + 8 + SmallBytes;

    public static async Task<T?> ReadAsync<T>(Stream body, long? contentLength, long maxBytes, CancellationToken cancellationToken) where T : class
    {
        if (contentLength > maxBytes)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(buffer.ToArray(), GitWriterHost.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
