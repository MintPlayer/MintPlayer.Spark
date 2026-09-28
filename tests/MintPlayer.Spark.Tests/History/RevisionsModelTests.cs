using System.Text.Json.Nodes;
using MintPlayer.Spark.Tests.Builder;

namespace MintPlayer.Spark.Tests.History;

/// <summary>
/// #460 T10: a hand-written <c>revisions</c> block in a model file survives model synchronization —
/// the synchronizer updates the existing definition in place, so an authored entity-level field rides
/// along (the same rule as <c>breadcrumb</c>).
/// </summary>
[Collection(ProcessExitCodeCollection.Name)]
public class RevisionsModelTests
{
    [Fact]
    public void Model_synchronization_keeps_a_hand_written_revisions_block()
    {
        using var scratch = new ScratchContentRoot("spark-revisions-sync-");
        scratch.Synchronize<HiSyncContext>();
        var file = Path.Combine(scratch.ModelPath, "HiSyncProbe.json");
        File.Exists(file).Should().BeTrue();

        var json = JsonNode.Parse(File.ReadAllText(file))!;
        json["persistentObject"]!["revisions"] = JsonNode.Parse("""{ "enabled": true, "minimumRevisionsToKeep": 2, "minimumRevisionAgeToKeep": "30.00:00:00", "purgeOnDelete": true }""");
        File.WriteAllText(file, json.ToJsonString());

        scratch.Synchronize<HiSyncContext>();

        var revisions = JsonNode.Parse(File.ReadAllText(file))!["persistentObject"]!["revisions"];
        revisions.Should().NotBeNull("synchronization never drops an authored block");
        revisions!["enabled"]!.GetValue<bool>().Should().BeTrue();
        revisions["minimumRevisionsToKeep"]!.GetValue<long>().Should().Be(2);
        revisions["minimumRevisionAgeToKeep"]!.GetValue<string>().Should().Be("30.00:00:00");
        revisions["purgeOnDelete"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void A_model_without_a_revisions_block_does_not_gain_one()
    {
        using var scratch = new ScratchContentRoot("spark-revisions-sync-");
        scratch.Synchronize<HiSyncContext>();

        var model = JsonNode.Parse(File.ReadAllText(Path.Combine(scratch.ModelPath, "HiSyncProbe.json")))!;

        model["persistentObject"]!.AsObject().ContainsKey("revisions").Should().BeFalse();
    }

    // Private and nested: a model type visible to assembly-wide scans would leak into other fixtures.
    private sealed class HiSyncProbe
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class HiSyncContext : SparkContext
    {
        public Raven.Client.Documents.Linq.IRavenQueryable<HiSyncProbe> Probes => Session.Query<HiSyncProbe>();
    }
}
