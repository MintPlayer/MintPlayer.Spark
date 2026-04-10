using MintPlayer.Spark.AllFeatures;
using MintPlayer.Spark.Authorization.Configuration;

namespace MintPlayer.Spark.Tests;

public class AuthorizationOptionsTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var options = new AuthorizationOptions();

        Assert.Equal("App_Data/security.json", options.SecurityFilePath);
        Assert.Equal(DefaultAccessBehavior.DenyAll, options.DefaultBehavior);
        Assert.True(options.CacheRights);
        Assert.Equal(5, options.CacheExpirationMinutes);
        Assert.True(options.EnableHotReload);
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

        Assert.Equal("custom/path.json", options.SecurityFilePath);
        Assert.Equal(DefaultAccessBehavior.AllowAll, options.DefaultBehavior);
        Assert.False(options.CacheRights);
        Assert.Equal(30, options.CacheExpirationMinutes);
        Assert.False(options.EnableHotReload);
    }
}

public class SparkFullOptionsTests
{
    [Fact]
    public void AllProperties_DefaultToNull()
    {
        var options = new SparkFullOptions();

        Assert.Null(options.Authorization);
        Assert.Null(options.Identity);
        Assert.Null(options.IdentityProviders);
        Assert.Null(options.Messaging);
        Assert.Null(options.Replication);
    }

    [Fact]
    public void Authorization_CanBeSet()
    {
        var options = new SparkFullOptions();
        var invoked = false;

        options.Authorization = _ => invoked = true;
        options.Authorization.Invoke(new AuthorizationOptions());

        Assert.True(invoked);
    }

    [Fact]
    public void Replication_CanBeSet()
    {
        var options = new SparkFullOptions();
        var invoked = false;

        options.Replication = _ => invoked = true;

        Assert.NotNull(options.Replication);
        options.Replication.Invoke(new MintPlayer.Spark.Replication.Abstractions.Configuration.SparkReplicationOptions
        {
            ModuleName = "Test",
            ModuleUrl = "https://localhost:5000"
        });
        Assert.True(invoked);
    }

    [Fact]
    public void Messaging_CanBeSet()
    {
        var options = new SparkFullOptions();
        var invoked = false;

        options.Messaging = _ => invoked = true;

        Assert.NotNull(options.Messaging);
        options.Messaging.Invoke(new MintPlayer.Spark.Messaging.SparkMessagingOptions());
        Assert.True(invoked);
    }
}
