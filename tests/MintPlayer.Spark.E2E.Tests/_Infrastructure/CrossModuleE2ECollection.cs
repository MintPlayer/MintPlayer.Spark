using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// A second Fleet host, running with <see cref="SparkReplicationCertificateMode.Development"/>.
/// <para>
/// It exists because the two questions need different hosts. The shared <see cref="FleetE2ECollection"/>
/// host runs in <c>Production</c> mode and proves that a caller presenting no client certificate is
/// refused — <c>401</c>, at the gate. Everything <i>after</i> a caller authenticates (module
/// registration, <c>security.json</c> rights, the write chokepoint) is unreachable there, because
/// nothing gets past the gate without a certificate.
/// </para>
/// <para>
/// Relaxing the shared host instead would have been cheaper and wrong: it turns that <c>401</c> into
/// a <c>403</c> and quietly deletes the only end-to-end coverage of the certificate requirement. A
/// second host costs one more startup and keeps both properties provable.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public class CrossModuleE2ECollection : ICollectionFixture<CrossModuleE2EFixture>
{
    public const string Name = "CrossModuleE2E";
}

public sealed class CrossModuleE2EFixture : IAsyncLifetime
{
    // A distinct environment name, not just distinct settings: both hosts write an
    // appsettings.{Environment}.json into the Fleet project directory and delete it on dispose, so
    // sharing the name would have them clobbering each other's configuration.
    public FleetTestHost Host { get; } = new()
    {
        EnvironmentName = "E2EModules",
        CertificateMode = SparkReplicationCertificateMode.Development,
    };

    public Task InitializeAsync() => Host.InitializeAsync();
    public Task DisposeAsync() => Host.DisposeAsync();
}
