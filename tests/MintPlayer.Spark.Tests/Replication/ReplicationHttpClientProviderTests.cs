using Microsoft.Extensions.Options;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// In-process coverage for the outbound replication HttpClient provider: per-target
/// caching and disposal. The cert-file branch needs a real PFX on disk and is left to
/// integration coverage; these exercise the default (no-cert) path.
/// </summary>
public class ReplicationHttpClientProviderTests
{
    private static ReplicationHttpClientProvider NewProvider(SparkReplicationCertificateOptions? cert = null) =>
        new(Options.Create(new SparkReplicationOptions
        {
            ModuleName = "Fleet",
            ModuleUrl = "https://localhost:5001",
            ClientCertificate = cert ?? new SparkReplicationCertificateOptions(),
        }));

    [Fact]
    public void GetClient_returns_a_client_and_caches_one_per_target()
    {
        using var provider = NewProvider();

        var hr1 = provider.GetClient("HR");
        var hr2 = provider.GetClient("HR");
        var other = provider.GetClient("Billing");

        hr1.Should().NotBeNull();
        hr2.Should().BeSameAs(hr1, "the provider caches a single HttpClient per target module");
        other.Should().NotBeSameAs(hr1, "different targets get their own cached client");
    }

    [Fact]
    public void GetClient_is_case_insensitive_on_target_name()
    {
        using var provider = NewProvider();
        provider.GetClient("HR").Should().BeSameAs(provider.GetClient("hr"));
    }

    [Fact]
    public void Dispose_releases_the_cache_so_a_later_call_creates_a_fresh_client()
    {
        var provider = NewProvider();
        var before = provider.GetClient("HR");

        provider.Dispose();

        var after = provider.GetClient("HR");
        after.Should().NotBeSameAs(before, "Dispose disposes and clears the per-target cache");
        provider.Dispose();
    }
}
