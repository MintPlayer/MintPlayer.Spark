using System.Text.Json.Nodes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Tests.Builder;

namespace MintPlayer.Spark.Tests.Expiration;

/// <summary>
/// X1: a hand-written <c>expiresAfter</c> in a model file survives model synchronization, and a model
/// without one does not gain one — the same rule as <c>revisions</c> and <c>breadcrumb</c>.
/// </summary>
[Collection(ProcessExitCodeCollection.Name)]
public class ExpiresAfterModelTests
{
    [Fact]
    public void Model_synchronization_keeps_a_hand_written_expiresAfter()
    {
        using var scratch = new ScratchContentRoot("spark-expiration-sync-");
        scratch.Synchronize<ExSyncContext>();
        var file = Path.Combine(scratch.ModelPath, "ExSyncProbe.json");
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        json["persistentObject"]!.AsObject().ContainsKey("expiresAfter").Should().BeFalse("nothing is generated");

        json["persistentObject"]!["expiresAfter"] = "7.00:00:00";
        File.WriteAllText(file, json.ToJsonString());
        scratch.Synchronize<ExSyncContext>();

        JsonNode.Parse(File.ReadAllText(file))!["persistentObject"]!["expiresAfter"]!.GetValue<string>().Should().Be("7.00:00:00");
    }

    // Private and nested: a model type visible to assembly-wide scans would leak into other fixtures.
    private sealed class ExSyncProbe : IExpiring
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime? ExpiresAtUtc { get; set; }
    }

    private sealed class ExSyncContext : SparkContext
    {
        public Raven.Client.Documents.Linq.IRavenQueryable<ExSyncProbe> Probes => Session.Query<ExSyncProbe>();
    }
}
