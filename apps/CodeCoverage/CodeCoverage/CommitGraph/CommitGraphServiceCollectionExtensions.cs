using CodeCoverage.CommitGraph;
using Microsoft.Extensions.Options;

namespace CodeCoverage;

/// <summary>
/// The commit graph's registrations that a <c>[Register]</c> attribute cannot express: the
/// options, the typed sidecar client and the avatar image client.
/// </summary>
public static class CommitGraphServiceCollectionExtensions
{
    public static IServiceCollection AddCodeCoverageCommitGraph(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CommitGraphOptions>(configuration.GetSection(CommitGraphOptions.Section));

        // No base address when CommitGraph:SidecarUrl is unset, which is how the client reports the
        // feature as off (local development, tests that do not need it). No client-wide timeout:
        // each call is bounded by its own, a fetch by its quota (D19).
        services.AddHttpClient<ICommitGraphSidecar, CommitGraphSidecarClient>((provider, http) =>
        {
            var url = provider.GetRequiredService<IOptions<CommitGraphOptions>>().Value.SidecarUrl;
            if (!string.IsNullOrWhiteSpace(url)) http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
            http.Timeout = Timeout.InfiniteTimeSpan;
        });

        // Avatar images: a short timeout, and no redirects, so an allow-listed URL can never be
        // bounced to another host (CommitGraphAvatars checks the host before every fetch).
        services.AddHttpClient(CommitGraphAvatars.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        return services;
    }
}
