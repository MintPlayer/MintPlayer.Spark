using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Licensing;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using MintPlayer.Spark.Replication.Services;
using NSubstitute;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary><see cref="SparkChangeVector.Compare"/>: the push-direction version guard (WD18).</summary>
public class SparkChangeVectorTests
{
    [Theory]
    [InlineData("A:5-db1", "A:5-db1", SparkChangeVectorOrder.Equal)]
    [InlineData("A:6-db1", "A:5-db1", SparkChangeVectorOrder.Newer)]
    [InlineData("A:4-db1", "A:5-db1", SparkChangeVectorOrder.Older)]
    [InlineData("A:5-db1, B:3-db2", "A:5-db1", SparkChangeVectorOrder.Newer)]
    [InlineData("A:5-db1", "A:5-db1, B:3-db2", SparkChangeVectorOrder.Older)]
    [InlineData("A:6-db1, B:2-db2", "A:5-db1, B:3-db2", SparkChangeVectorOrder.Concurrent)]
    [InlineData("B:3-db2, A:5-db1", "A:5-db1, B:3-db2", SparkChangeVectorOrder.Equal)]
    [InlineData("A:1-db1", null, SparkChangeVectorOrder.Newer)]
    [InlineData(null, "A:1-db1", SparkChangeVectorOrder.Older)]
    [InlineData(null, "", SparkChangeVectorOrder.Equal)]
    public void Compare_orders_single_and_multi_entry_vectors(string? first, string? second, SparkChangeVectorOrder expected)
        => SparkChangeVector.Compare(first, second).Should().Be(expected);

    [Fact]
    public void A_malformed_entry_is_refused()
    {
        var act = () => SparkChangeVector.Compare("not-a-vector", "A:1-db1");
        act.Should().Throw<FormatException>();
    }
}

/// <summary>
/// <see cref="ManifestProjector"/>: the HTTP transport's projection gives what the generated script would (a missing
/// last member is omitted, a missing or null intermediate member gives null). The ETL side of the equivalence is
/// <see cref="ManifestProjectorEquivalenceTests"/> (Developer licence).
/// </summary>
public class ManifestProjectorTests
{
    private static readonly ReplicaManifest Manifest = new("ReplSourceCars", "ReplicaCars",
    [
        new ReplicaField("LicensePlate", ["LicensePlate"]),
        new ReplicaField("OwnerName", ["Owner", "Name"]),
    ]);

    private static JsonElement Source(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Plain_and_nested_paths_are_copied()
    {
        var result = ManifestProjector.Project(Manifest, Source("""{"LicensePlate":"1-ABC","Owner":{"Name":"Ann"},"Mileage":5}"""));

        result.ToJsonString().Should().Be("""{"LicensePlate":"1-ABC","OwnerName":"Ann"}""");
    }

    [Fact]
    public void A_null_or_missing_intermediate_member_gives_null()
    {
        ManifestProjector.Project(Manifest, Source("""{"LicensePlate":"1-ABC","Owner":null}""")).ToJsonString()
            .Should().Be("""{"LicensePlate":"1-ABC","OwnerName":null}""");
        ManifestProjector.Project(Manifest, Source("""{"LicensePlate":"1-ABC"}""")).ToJsonString()
            .Should().Be("""{"LicensePlate":"1-ABC","OwnerName":null}""");
    }

    [Fact]
    public void A_missing_last_member_is_omitted_like_undefined()
    {
        ManifestProjector.Project(Manifest, Source("""{"Owner":{}}""")).ToJsonString().Should().Be("{}");
    }

    [Fact]
    public void Arrays_and_objects_are_copied_whole()
    {
        var manifest = new ReplicaManifest("S", "T", [new ReplicaField("Tags", ["Tags"])]);
        ManifestProjector.Project(manifest, Source("""{"Tags":[{"A":1},{"A":2}]}""")).ToJsonString()
            .Should().Be("""{"Tags":[{"A":1},{"A":2}]}""");
    }
}

/// <summary>The retry ladder (1 m / 5 m / 30 m / 2 h, last repeats) and the readable manifest diff.</summary>
public class ReplicationHelpersTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.5, 1)]
    [InlineData(1, 5)]
    [InlineData(5.9, 5)]
    [InlineData(6, 30)]
    [InlineData(36, 120)]
    [InlineData(10_000, 120)]
    public void The_ladder_steps_by_elapsed_time(double elapsedMinutes, double expectedMinutes)
        => RetryLadder.Next(ReplicationPushOptions.DefaultRetryLadder, TimeSpan.FromMinutes(elapsedMinutes))
            .Should().Be(TimeSpan.FromMinutes(expectedMinutes));

    /// <summary>F14: the budget covers 48 hours of the ladder (today's retries ended after about 5–8 minutes).</summary>
    [Fact]
    public void The_budget_outlasts_the_give_up_time()
        => RetryLadder.Budget(ReplicationPushOptions.DefaultRetryLadder, TimeSpan.FromHours(48)).Should().BeGreaterThan(25);

    [Fact]
    public void The_diff_names_added_removed_and_resourced_fields()
    {
        var approved = new ReplicaManifest("Cars", "Cars", [new ReplicaField("Plate", ["Plate"]), new ReplicaField("Color", ["Color"])]);
        var requested = new ReplicaManifest("Cars", "Cars", [new ReplicaField("Plate", ["Registration", "Plate"]), new ReplicaField("Model", ["Model"])]);

        ManifestDiff.Describe(approved, requested).Should().Be("~ Plate ← Registration.Plate (was Plate)\n+ Model\n- Color");
    }

    [Fact]
    public void A_first_request_lists_every_field()
        => ManifestDiff.Describe(null, new ReplicaManifest("Cars", "Cars", [new ReplicaField("Plate", ["Plate"])]))
            .Should().Be("New request: Cars → Cars\n+ Plate");
}

/// <summary>WD17/F18: the transport an owner delivers with, chosen by the licence.</summary>
public class ReplicationTransportResolverTests
{
    private static ReplicationTransportResolver Resolver(ReplicationTransport configured, bool? hasEtl)
    {
        var options = ReplicationFixtures.Options("Fleet");
        options.Transport = configured;
        var licence = Substitute.For<ISparkLicenseCapabilities>();
        licence.GetAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<SparkLicenseStatus?>(
            hasEtl is { } etl ? new SparkLicenseStatus { Type = etl ? "Developer" : "Community", HasRavenEtl = etl } : null));
        return new ReplicationTransportResolver(Microsoft.Extensions.Options.Options.Create(options), licence, NullLogger<ReplicationTransportResolver>.Instance);
    }

    [Fact]
    public async Task Http_pinned_is_Http_whatever_the_licence()
        => (await Resolver(ReplicationTransport.Http, hasEtl: true).ResolveAsync()).Should().Be(ReplicationTransport.Http);

    [Fact]
    public async Task Etl_pinned_without_the_licence_refuses_with_the_fix()
    {
        var act = () => Resolver(ReplicationTransport.Etl, hasEtl: false).ResolveAsync();
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("'Http'");
    }

    [Fact]
    public async Task Auto_with_an_unreadable_licence_is_Http()
        => (await Resolver(ReplicationTransport.Auto, hasEtl: null).ResolveAsync()).Should().Be(ReplicationTransport.Http);

    [Fact]
    public async Task Auto_with_ETL_in_the_licence_is_Etl()
        => (await Resolver(ReplicationTransport.Auto, hasEtl: true).ResolveAsync()).Should().Be(ReplicationTransport.Etl);
}

/// <summary>
/// <see cref="ReplicaCatalog"/>: the generated constants are read as they are, and startup refuses a replica it cannot
/// serve (warehouse §4.2.1).
/// </summary>
public class ReplicaCatalogTests
{
    private static readonly IDocumentStore Conventions = new DocumentStore();

    [Fact]
    public void The_fixture_constants_render_like_the_generator()
    {
        ReplicaScriptRenderer.TryParse(ReplicaCar.ReplicationManifest, out var manifest, out _).Should().BeTrue();
        ReplicaScriptRenderer.RenderScript(manifest!).Should().Be(ReplicaCar.ReplicationScript);
        ReplicaScriptRenderer.Hash(ReplicaCar.ReplicationManifest, ReplicaCar.ReplicationScript).Should().Be(ReplicaCar.ReplicationHash);
        ReplicaScriptRenderer.Hash(ReplicaCarNarrow.Manifest, ReplicaCarNarrow.Script).Should().Be(ReplicaCarNarrow.Hash);
    }

    [Fact]
    public void A_replica_is_described_from_its_constants()
    {
        var descriptor = ReplicationFixtures.Catalog().All.Should().ContainSingle().Which;

        descriptor.SourceCollection.Should().Be("ReplSourceCars");
        descriptor.TargetCollection.Should().Be("ReplicaCars");
        descriptor.Sources.Should().BeEquivalentTo(["Fleet"]);
        descriptor.WriteBack.Should().BeTrue();
        descriptor.Hash.Should().Be(ReplicaCar.ReplicationHash);
        // A nested path cannot be written back.
        descriptor.WritableFields.Keys.Should().BeEquivalentTo(["LicensePlate", "Model"]);
    }

    [Fact]
    public void The_catalog_is_the_replica_type_guard()
    {
        var catalog = ReplicationFixtures.Catalog();
        catalog.IsReplica(typeof(ReplicaCar)).Should().BeTrue();
        catalog.IsReplica(typeof(ReplSourceCar)).Should().BeFalse();
    }

    [Fact]
    public void A_consistent_replica_raises_no_problem()
        => ReplicationFixtures.Catalog().Problems(Conventions).Should().BeEmpty();

    [Fact]
    public void The_set_hash_changes_with_the_replicas_of_an_owner()
    {
        var catalog = ReplicationFixtures.Catalog();
        catalog.SetHash("Fleet").Should().StartWith("sha256:");
        catalog.SetHash("Fleet").Should().Be(catalog.SetHash("Fleet"));
        catalog.SetHash("HR").Should().NotBe(catalog.SetHash("Fleet"), "HR is the source of nothing here");
        catalog.Owners.Should().BeEquivalentTo(["Fleet"]);
    }
}
