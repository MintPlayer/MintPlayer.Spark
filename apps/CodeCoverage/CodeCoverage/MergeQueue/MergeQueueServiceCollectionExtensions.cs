using CodeCoverage.MergeQueue;
using Microsoft.Extensions.Options;

namespace CodeCoverage;

/// <summary>The merge queue's registrations that a <c>[Register]</c> attribute cannot express: the options and the typed
/// git writer client (M3). The saga (M5) adds its own.</summary>
public static class MergeQueueServiceCollectionExtensions
{
    public static IServiceCollection AddCodeCoverageMergeQueue(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MergeQueueOptions>(configuration.GetSection(MergeQueueOptions.Section));

        // No base address when MergeQueue:GitWriterUrl is unset, which is how the client reports the git steps as off.
        // No client-wide timeout: each job request is bounded by MergeQueue:JobRequestTimeoutSeconds.
        services.AddHttpClient<IGitWriterSidecar, GitWriterSidecarClient>((provider, http) =>
        {
            var url = provider.GetRequiredService<IOptions<MergeQueueOptions>>().Value.GitWriterUrl;
            if (!string.IsNullOrWhiteSpace(url)) http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
            http.Timeout = Timeout.InfiniteTimeSpan;
        });
        return services;
    }
}
