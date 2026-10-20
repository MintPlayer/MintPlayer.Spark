using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;

namespace MintPlayer.Spark.Webhooks.GitHub.Endpoints;

/// <summary>
/// <c>POST {WebhookPath}</c> (default <see cref="DefaultPath"/>): GitHub's webhook deliveries, as the
/// <see cref="GitHubWebhookSource.Name"/> source of <c>MintPlayer.Spark.Webhooks</c>. 200 when the
/// delivery is verified and handed to the processor; 401 when <c>X-Hub-Signature-256</c> does not
/// match (or no secret is configured); 413 over 25 MB (GitHub's own cap); 415 when the body is not
/// <c>application/json</c>; 500 when the processor throws, so GitHub retries.
/// </summary>
/// <remarks>
/// It used to be Octokit's hand-mapped <c>MapGitHubWebhooks</c>, the one route outside the endpoint
/// generator, because Octokit owned the request-level signature refusal. The generic receiver now
/// owns it (<c>docs/endpoints_generator_webhooks_exception.md</c>), over the exact bytes read here.
/// </remarks>
internal sealed partial class GitHubWebhookEndpoint : IPostEndpoint<WebhookRequest>
{
    /// <summary>The route when <see cref="GitHubWebhooksOptions.WebhookPath"/> is not changed; that option defaults to this.</summary>
    public const string DefaultPath = "/api/github/webhooks";

    public static string Path => DefaultPath;

    /// <summary>The host's <see cref="GitHubWebhooksOptions.WebhookPath"/>. Read once, at map time, from the root provider.</summary>
    static string? IEndpointBase.GetPath(IServiceProvider services)
        => services.GetRequiredService<IOptions<GitHubWebhooksOptions>>().Value.WebhookPath;

    // ⚠️ EXPLICITLY exempt: GitHub POSTs without a browser session or an antiforgery token. Every
    // delivery is authenticated by its X-Hub-Signature-256 instead.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));

    [Inject] private readonly IWebhookReceiver receiver;

    protected override async ValueTask<WebhookRequest?> BindRequestAsync(HttpContext context)
        => await receiver.ReadAsync(GitHubWebhookSource.Name, WebhookRoute.Own, context.Request.Headers, context.Request.Body, context.Request.ContentLength, context.RequestAborted);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Results.StatusCode(failure?.StatusCode ?? StatusCodes.Status400BadRequest));

    public override Task<IResult> HandleAsync(WebhookRequest request, CancellationToken cancellationToken)
        => receiver.ReceiveAsync(GitHubWebhookSource.Name, request, cancellationToken);
}
