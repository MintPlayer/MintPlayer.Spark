using MintPlayer.Spark.AllFeatures;
using MintPlayer.Spark.Authorization.Configuration;

namespace MintPlayer.Spark.Tests;

public class AuthorizationOptionsTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var options = new AuthorizationOptions();

        options.SecurityFilePath.Should().Be("App_Data/security.json");
        options.DefaultBehavior.Should().Be(DefaultAccessBehavior.DenyAll);
        options.CacheRights.Should().BeTrue();
        options.CacheExpirationMinutes.Should().Be(5);
        options.EnableHotReload.Should().BeTrue();
    }

    [Fact]
    public void Properties_CanBeOverridden()
    {
        var options = new AuthorizationOptions
        {
            SecurityFilePath = "custom/path.json",
            DefaultBehavior = DefaultAccessBehavior.AllowAll,
            CacheRights = false,
            CacheExpirationMinutes = 30,
            EnableHotReload = false
        };

        options.SecurityFilePath.Should().Be("custom/path.json");
        options.DefaultBehavior.Should().Be(DefaultAccessBehavior.AllowAll);
        options.CacheRights.Should().BeFalse();
        options.CacheExpirationMinutes.Should().Be(30);
        options.EnableHotReload.Should().BeFalse();
    }
}

public class SparkFullOptionsTests
{
    [Fact]
    public void AllProperties_DefaultToNull()
    {
        var options = new SparkFullOptions();

        options.Authorization.Should().BeNull();
        options.Identity.Should().BeNull();
        options.IdentityProviders.Should().BeNull();
        options.Messaging.Should().BeNull();
        options.Replication.Should().BeNull();
    }

    [Fact]
    public void Authorization_CanBeSet()
    {
        var options = new SparkFullOptions();
        var invoked = false;

        options.Authorization = _ => invoked = true;
        options.Authorization!.Invoke(new AuthorizationOptions());

        invoked.Should().BeTrue();
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
