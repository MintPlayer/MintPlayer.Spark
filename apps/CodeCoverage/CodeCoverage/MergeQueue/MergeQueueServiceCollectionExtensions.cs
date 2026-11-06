using CodeCoverage.MergeQueue;
using Microsoft.Extensions.Options;

namespace CodeCoverage;

/// <summary>The merge queue's registrations that a <c>[Register]</c> attribute cannot express: the options and the typed
/// git writer client (M3). The saga (M5) adds its own.</summary>
public static class MergeQueueServiceCollectionExtensions
{
    /// <summary>The base address of the in-process Development writer; only its TestServer handler answers it.</summary>
    private static readonly Uri InProcessWriter = new("http://git-writer.in-process/");

    public static IServiceCollection AddCodeCoverageMergeQueue(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<MergeQueueOptions>(configuration.GetSection(MergeQueueOptions.Section));
        var sidecarUrl = configuration.GetSection(MergeQueueOptions.Section)[nameof(MergeQueueOptions.GitWriterUrl)];
        var inProcess = InProcessWriterApplies(environment, sidecarUrl);

        // No base address when MergeQueue:GitWriterUrl is unset (outside Development), which is how the client reports the
        // git steps as off. No client-wide timeout: each job request is bounded by MergeQueue:JobRequestTimeoutSeconds.
        var client = services.AddHttpClient<IGitWriterSidecar, GitWriterSidecarClient>((provider, http) =>
        {
            var url = provider.GetRequiredService<IOptions<MergeQueueOptions>>().Value.GitWriterUrl;
            if (!string.IsNullOrWhiteSpace(url)) http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
            else if (inProcess) http.BaseAddress = InProcessWriter;
            http.Timeout = Timeout.InfiniteTimeSpan;
        });
#if DEBUG
        if (inProcess)
        {
            services.AddSingleton<DevelopmentGitWriter>();
            services.AddHostedService(sp => sp.GetRequiredService<DevelopmentGitWriter>());
            client.ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<DevelopmentGitWriter>().CreateHandler());
        }
#endif
        return services;
    }

    /// <summary>
    /// Development with no sidecar URL runs the writer in-process (<c>DevelopmentGitWriter</c>, Debug builds only). A URL
    /// always wins, so a locally started sidecar can still be tested; production always goes through the sidecar.
    /// </summary>
    internal static bool InProcessWriterApplies(IHostEnvironment environment, string? sidecarUrl)
    {
#if DEBUG
        return environment.IsDevelopment() && string.IsNullOrWhiteSpace(sidecarUrl);
#else
        return false;
#endif
    }
}
