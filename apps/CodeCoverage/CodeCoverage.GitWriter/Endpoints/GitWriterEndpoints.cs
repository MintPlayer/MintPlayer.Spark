using System.Text.Json;
using CodeCoverage.GitWriter.Jobs;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.GitWriter.Endpoints;

// The writer's internal API. Reachable only on the coverage-git-writer network; every value is validated before it reaches
// a path, an argument or the environment, and a refusal is a bare 400. Every job's answer is 200 with an outcome.

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
        => await Body.ReadAsync<PrepareRequestWire>(context, Body.SmallBytes);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(Results.BadRequest());

    public override async Task<IResult> HandleAsync(PrepareRequestWire request, CancellationToken cancellationToken)
    {
        var insecure = options.Value.AllowInsecureHttp;
        if (!WireValidation.IsRepo(request.Provider, request.RepoId)
            || WireValidation.RemoteUrl(request.CloneUrl, insecure) is not { } cloneUrl
            || WireValidation.Credential(request.Credential) is not { } credential
            || WireValidation.Branch(request.BaseBranch) is not { } baseBranch
            || (request.BaseSha is not null && WireValidation.ObjectId(request.BaseSha) is null)
            || WireValidation.HeadRef(request.HeadRef) is not { } headRef
            || WireValidation.ObjectId(request.HeadSha) is not { } headSha
            || WireValidation.QueueBranch(request.QueueBranch) is not { } queueBranch
            || (request.ExpectedQueueSha is not null && WireValidation.ObjectId(request.ExpectedQueueSha) is null)
            || WireValidation.Rules(request.AutoResolve) is not { } rules
            || WireValidation.Identity(request.Author) is not { } author
            || WireValidation.Identity(request.Committer) is not { } committer
            || WireValidation.Message(request.Message) is not { } message)
            return Results.BadRequest();

        var job = new PrepareJob($"{request.Provider}/{request.RepoId}", cloneUrl, credential, baseBranch, request.BaseSha, headRef, headSha,
            queueBranch, request.ExpectedQueueSha, rules, author, committer, message);
        return Results.Json(await writer.PrepareAsync(job), GitWriterHost.Json);
    }
}

/// <summary><c>POST /jobs/publish</c>: §5.1 step [3].</summary>
internal sealed partial class GitWriterPublishEndpoint : IPostEndpoint<PublishRequestWire>
{
    public static string Path => "/jobs/publish";

    [Inject] private readonly WriterService writer;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<PublishRequestWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<PublishRequestWire>(context, Body.PatchBytes(options.Value.MaxPatchBytes));

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(Results.BadRequest());

    public override async Task<IResult> HandleAsync(PublishRequestWire request, CancellationToken cancellationToken)
    {
        var insecure = options.Value.AllowInsecureHttp;
        if (!WireValidation.IsRepo(request.Provider, request.RepoId)
            || WireValidation.RemoteUrl(request.CloneUrl, insecure) is not { } cloneUrl
            || WireValidation.Credential(request.Credential) is not { } credential
            || WireValidation.QueueBranch(request.QueueBranch) is not { } queueBranch
            || WireValidation.ObjectId(request.PreparedSha) is not { } preparedSha
            || WireValidation.HeadRef(request.HeadRef) is not { } headRef
            || WireValidation.Branch(request.PrBranch) is not { } prBranch
            || WireValidation.ObjectId(request.ExpectedPrHeadSha) is not { } expectedHead
            || WireValidation.Globs(request.RegeneratedPaths) is not { } globs
            || WireValidation.Identity(request.Committer) is not { } committer)
            return Results.BadRequest();
        // A fork's branch is pushed to the fork (O3); otherwise to the base repository.
        var pushUrl = request.PushUrl is null ? cloneUrl : WireValidation.RemoteUrl(request.PushUrl, insecure);
        if (pushUrl is null)
            return Results.BadRequest();
        byte[] patch = [];
        if (!string.IsNullOrEmpty(request.Patch))
        {
            try
            {
                patch = Convert.FromBase64String(request.Patch);
            }
            catch (FormatException)
            {
                return Results.BadRequest();
            }
        }

        var job = new PublishJob($"{request.Provider}/{request.RepoId}", cloneUrl, pushUrl, credential, queueBranch, preparedSha, headRef,
            prBranch, expectedHead, patch, globs, committer);
        return Results.Json(await writer.PublishAsync(job), GitWriterHost.Json);
    }
}

/// <summary><c>DELETE /jobs/queue-branch</c>: removes <c>mq/…</c> after its entry ended (D5). The body names it.</summary>
internal sealed partial class GitWriterDeleteQueueBranchEndpoint : IDeleteEndpoint<DeleteQueueBranchWire, DeleteQueueBranchResponse>
{
    public static string Path => "/jobs/queue-branch";

    [Inject] private readonly WriterService writer;
    [Inject] private readonly IOptions<GitWriterOptions> options;

    protected override async ValueTask<DeleteQueueBranchWire?> BindRequestAsync(HttpContext context)
        => await Body.ReadAsync<DeleteQueueBranchWire>(context, Body.SmallBytes);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(Results.BadRequest());

    public override async Task<IResult> HandleAsync(DeleteQueueBranchWire request, CancellationToken cancellationToken)
    {
        if (!WireValidation.IsRepo(request.Provider, request.RepoId)
            || WireValidation.RemoteUrl(request.CloneUrl, options.Value.AllowInsecureHttp) is not { } cloneUrl
            || WireValidation.Credential(request.Credential) is not { } credential
            || WireValidation.QueueBranch(request.QueueBranch) is not { } queueBranch
            || (request.ExpectedSha is not null && WireValidation.ObjectId(request.ExpectedSha) is null))
            return Results.BadRequest();
        var job = new DeleteQueueBranchJob($"{request.Provider}/{request.RepoId}", cloneUrl, credential, queueBranch, request.ExpectedSha);
        return Results.Json(await writer.DeleteQueueBranchAsync(job), GitWriterHost.Json);
    }
}

/// <summary>Request bodies, read under a byte cap; anything too large, malformed or JSON <c>null</c> is null (a bare 400).</summary>
internal static class Body
{
    public const int SmallBytes = 256 * 1024;

    /// <summary>A publish body: the base64 patch (4/3 of its bytes) plus the rest of the request.</summary>
    public static long PatchBytes(long maxPatchBytes) => maxPatchBytes / 3 * 4 + 8 + SmallBytes;

    public static async Task<T?> ReadAsync<T>(HttpContext context, long maxBytes) where T : class
    {
        if (context.Request.ContentLength > maxBytes)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
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
