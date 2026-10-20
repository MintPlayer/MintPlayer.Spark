using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Webhooks.Endpoints;

/// <summary>
/// <c>POST /spark/webhooks/{source}</c>: the shared route of every source registered with
/// <see cref="WebhookSourceBuilder.SharedRoute"/> (the default). 200 when the delivery is verified
/// and enqueued; 401 when verification fails; 404 for an unknown source; 413 over the source's body
/// cap; 415 for a content type the source does not accept. Mapped only when such a source exists.
/// </summary>
/// <remarks>
/// The body is read raw in <see cref="BindRequestAsync"/>, the endpoint library's typed-binding
/// hook: a webhook's request is its exact bytes, and binding it as JSON would destroy the bytes the
/// provider signed.
/// </remarks>
internal sealed partial class ReceiveWebhook : IPostEndpoint<WebhookRequest>
{
    public static string Path => "/spark/webhooks/{source}";

    static bool IEndpointBase.IsEnabled(IServiceProvider services)
        => services.GetService<WebhookSourceCatalog>()?.HasSharedRouteSource == true;

    // ⚠️ EXPLICITLY exempt: a provider's server POSTs here, with no browser, no cookie and no
    // antiforgery token. Every delivery is authenticated by its source's verification instead.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));

    [RouteParam] public string Source { get; set; } = "";

    [Inject] private readonly IWebhookReceiver receiver;

    protected override async ValueTask<WebhookRequest?> BindRequestAsync(HttpContext context)
        => await receiver.ReadAsync(Source, WebhookRoute.Shared, context.Request.Headers, context.Request.Body, context.Request.ContentLength, context.RequestAborted);

    // A bare status: a provider reads nothing else, and a problem body would only describe the source.
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(Results.StatusCode(failure?.StatusCode ?? StatusCodes.Status400BadRequest));

    public override Task<IResult> HandleAsync(WebhookRequest request, CancellationToken cancellationToken)
        => receiver.ReceiveAsync(Source, request, cancellationToken);
}
