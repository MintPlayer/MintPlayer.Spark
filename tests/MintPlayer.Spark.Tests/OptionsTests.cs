using MintPlayer.Spark.AllFeatures;

namespace MintPlayer.Spark.Tests;

public class SparkFullOptionsTests
{
    [Fact]
    public void AllProperties_DefaultToNull()
    {
        var options = new SparkFullOptions();

        options.Identity.Should().BeNull();
        options.IdentityProviders.Should().BeNull();
        options.Messaging.Should().BeNull();
        options.Replication.Should().BeNull();
    }

    [Fact]
    public void Replication_CanBeSet()
    {
        var options = new SparkFullOptions();
        var invoked = false;

        options.Replication = _ => invoked = true;

        options.Replication.Should().NotBeNull();
        options.Replication!.Invoke(new MintPlayer.Spark.Replication.Abstractions.Configuration.SparkReplicationOptions
        {
            ModuleName = "Test",
            ModuleUrl = "https://localhost:5000"
        });
        invoked.Should().BeTrue();
    }

    [Fact]
    public void Messaging_CanBeSet()
    {
        var options = new SparkFullOptions();
        var invoked = false;

        options.Messaging = _ => invoked = true;

        options.Messaging.Should().NotBeNull();
        options.Messaging!.Invoke(new MintPlayer.Spark.Messaging.SparkMessagingOptions());
        invoked.Should().BeTrue();
    }
}
