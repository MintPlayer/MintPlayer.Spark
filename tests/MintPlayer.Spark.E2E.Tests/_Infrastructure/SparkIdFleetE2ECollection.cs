namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// SparkId issues, Fleet validates: the real token topology, in two processes
/// (<c>docs/identity_provider_platform_PRD.md</c> I0, multi-host plan R3). Fleet used to host an issuer
/// of its own for this round trip.
/// </summary>
[CollectionDefinition(Name)]
public class SparkIdFleetE2ECollection : ICollectionFixture<SparkIdFleetE2EFixture>
{
    public const string Name = "SparkIdFleetE2E";
}

public sealed class SparkIdFleetE2EFixture : IAsyncLifetime
{
    public SparkIdTestHost SparkId { get; } = new();

    /// <summary>Created once SparkId runs, because Fleet's configuration needs SparkId's URL.</summary>
    public FleetTestHost Fleet { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await SparkId.InitializeAsync();

        // A distinct environment name: every Fleet host writes appsettings.{Environment}.json into the
        // Fleet project directory and deletes it on dispose, so sharing "E2E" would clobber the
        // shared host's configuration.
        Fleet = new FleetTestHost
        {
            EnvironmentName = "E2ESparkId",
            JwtBearerAuthority = SparkId.Issuer,
        };
        await Fleet.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (Fleet is not null) await Fleet.DisposeAsync();
        await SparkId.DisposeAsync();
    }
}
