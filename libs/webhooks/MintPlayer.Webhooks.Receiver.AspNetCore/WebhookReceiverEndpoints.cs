using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MintPlayer.Webhooks;

public sealed class WebhookReceiverOptions : WebhookVerifierOptions
{
    /// <summary>Larger bodies are refused with 413 before verification. Default 1 MiB.</summary>
    public long MaxBodyBytes { get; set; } = 1024 * 1024;
}

public static class WebhookReceiverServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TProcessor"/> (scoped, so it can take scoped services) and its receiver options.
    /// Several processors can live side by side, each with its own secrets.
    /// </summary>
    public static IServiceCollection AddWebhookReceiver<TProcessor>(this IServiceCollection services, Action<WebhookReceiverOptions> configure)
        where TProcessor : WebhookProcessor
    {
        services.TryAddScoped<TProcessor>();
        services.Configure(OptionsName<TProcessor>(), configure);
        services.TryAddSingleton<WebhookReceiverCache>();
        return services;
    }

    internal static string OptionsName<TProcessor>() => typeof(TProcessor).FullName!;
}

internal sealed partial class WebhookReceiverCache
{
    [MintPlayer.SourceGenerators.Attributes.Inject] private readonly IOptionsMonitor<WebhookReceiverOptions> options;

    private readonly Dictionary<string, (WebhookReceiverOptions Options, WebhookReceiver Receiver)> cache = new();
    private readonly Lock gate = new();

    public (WebhookReceiverOptions Options, WebhookReceiver Receiver) Get(string name)
    {
        lock (gate)
        {
            var current = options.Get(name);
            // Rebuild when the options instance changes (IOptionsMonitor reload), e.g. a rotated secret.
            if (cache.TryGetValue(name, out var entry) && ReferenceEquals(entry.Options, current))
                return entry;
            entry = (current, new WebhookReceiver(current));
            cache[name] = entry;
            return entry;
        }
    }
}

public static class WebhookReceiverEndpointRouteBuilderExtensions
{
    /// <summary>
    /// POST <paramref name="pattern"/>: verify, then dispatch to <typeparamref name="TProcessor"/>. Answers 204, 400
    /// (not a webhook), 401 (verification failed) or 413 (too large). The endpoint allows anonymous callers: the
    /// signature is the authentication.
    /// </summary>
    public static RouteHandlerBuilder MapWebhookReceiver<TProcessor>(this IEndpointRouteBuilder endpoints, string pattern)
        where TProcessor : WebhookProcessor
    {
        var name = WebhookReceiverServiceCollectionExtensions.OptionsName<TProcessor>();
        return endpoints.MapPost(pattern, async (HttpContext http) =>
        {
            var (options, receiver) = http.RequestServices.GetRequiredService<WebhookReceiverCache>().Get(name);

            var body = await ReadBodyAsync(http.Request, options.MaxBodyBytes, http.RequestAborted);
            if (body is null)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            var processor = http.RequestServices.GetRequiredService<TProcessor>();
            var headers = http.Request.Headers;
            var result = await receiver.HandleAsync(h => headers.TryGetValue(h, out var v) ? v.ToString() : null, body, processor, http.RequestAborted);
            return Results.StatusCode(result.StatusCode);
        })
        .AllowAnonymous()
        .DisableAntiforgery();
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, long max, CancellationToken cancellationToken)
    {
        if (request.ContentLength is long declared && declared > max)
            return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > max)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
