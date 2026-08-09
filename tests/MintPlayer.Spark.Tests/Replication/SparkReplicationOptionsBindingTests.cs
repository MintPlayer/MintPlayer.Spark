using Microsoft.Extensions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// How <c>Spark:Replication</c> binds — specifically the array properties.
/// <para>
/// <b>Why this exists.</b> `.NET`'s configuration binder does not replace a collection that already
/// has elements; it <i>appends</i> to it. So a property whose C# initializer is a non-empty array
/// keeps its default and gains the configured value <b>after</b> it. For
/// <see cref="SparkReplicationOptions.SparkModulesUrls"/> — whose first element is the one
/// <c>DocumentStore</c> talks to — that meant an app configuring the setting still pointed at the
/// hardcoded <c>http://localhost:8080</c>, silently, while appearing to be configured.
/// </para>
/// <para>
/// Found from the other end: cross-module E2E tests refused a module that was demonstrably
/// registered, because the two processes were reading different RavenDB servers.
/// </para>
/// </summary>
public class SparkReplicationOptionsBindingTests
{
    private static SparkReplicationOptions Bind(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var options = new SparkReplicationOptions { ModuleName = "Test", ModuleUrl = "https://localhost" };
        configuration.GetSection("Spark:Replication").Bind(options);
        return options;
    }

    [Fact]
    public void A_configured_SparkModulesUrls_replaces_the_default_rather_than_queueing_behind_it()
    {
        var options = Bind(
            ("Spark:Replication:SparkModulesUrls:0", "http://127.0.0.1:9999"));

        // The assertion that matters is the *first* element, because that is the server
        // DocumentStore actually connects to. Asserting only "contains the configured URL" would
        // have passed against the broken behaviour, which produced
        // {"http://localhost:8080", "http://127.0.0.1:9999"}.
        options.ResolvedSparkModulesUrls.Should().ContainSingle()
            .Which.Should().Be("http://127.0.0.1:9999",
                "an app that configures the modules URL must not still be pointed at the default");
    }

    [Fact]
    public void An_unconfigured_SparkModulesUrls_keeps_the_documented_default()
    {
        var options = Bind(("Spark:Replication:ModuleName", "Test"));

        options.ResolvedSparkModulesUrls.Should().ContainSingle()
            .Which.Should().Be(SparkReplicationOptions.DefaultSparkModulesUrl,
                "the convenience default is the point of having one — the fix must not remove it");
    }

    [Fact]
    public void Scalar_settings_bind_normally()
    {
        // The contrast that made the array bug hard to see: everything else in the same section
        // overrode correctly, so the section was plainly being read.
        var options = Bind(
            ("Spark:Replication:SparkModulesDatabase", "SparkModulesE2E"),
            ("Spark:Replication:ModuleUrl", "https://localhost:5999"));

        options.SparkModulesDatabase.Should().Be("SparkModulesE2E");
        options.ModuleUrl.Should().Be("https://localhost:5999");
    }
}
