using System.Text.Json;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// <c>triggersRefresh</c> is hand-authored in the model JSON and must survive synchronize, like
/// <c>editMode</c> and <c>referenceDisplayType</c> before it. Preservation here is structural rather
/// than declared: the synchronizer mutates the existing attribute object in place and reassigns only
/// a fixed set of fields, so a field it never assigns survives for free.
///
/// <para>
/// That freeness is exactly why the obvious test is worthless. An attribute the synchronizer does not
/// touch at all keeps the flag whether or not the update branch would have clobbered it — the branch
/// is never reached. The discriminating cases are the ones where synchronize <em>does</em> rewrite
/// the attribute: a changed <c>dataType</c> and a reassigned <c>order</c>.
/// </para>
/// </summary>
public class TriggersRefreshPreservationTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(
        Path.GetTempPath(), "spark-trigrefresh-" + Guid.NewGuid().ToString("N"));

    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();
    private readonly IIndexCatalog _indexCatalog = Substitute.For<IIndexCatalog>();

    public TriggersRefreshPreservationTests()
    {
        Directory.CreateDirectory(_contentRoot);
        _hostEnv.ContentRootPath.Returns(_contentRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string ModelDir => Path.Combine(_contentRoot, "App_Data", "Model");

    private void Synchronize() => new ModelSynchronizer(_hostEnv, _indexCatalog)
        .SynchronizeModels(typeof(TriggerContext));

    private void Seed(string fileName, string json)
    {
        Directory.CreateDirectory(ModelDir);
        File.WriteAllText(Path.Combine(ModelDir, fileName), json);
    }

    private JsonElement Attribute(string fileName, string attributeName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModelDir, fileName)));
        return document.RootElement.GetProperty("persistentObject").GetProperty("attributes")
            .EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == attributeName)
            .Clone();
    }

    private bool? TriggersRefresh(string fileName, string attributeName) =>
        Attribute(fileName, attributeName).TryGetProperty("triggersRefresh", out var flag)
            ? flag.GetBoolean()
            : null;

    [Fact]
    public void The_flag_survives_synchronize_on_an_untouched_attribute()
    {
        // The non-discriminating case, kept because its failure would mean something far worse than a
        // clobbering update branch: that synchronize drops unknown fields wholesale.
        Seed("TriggerProbe.json", MinimalSeed(name: "Status", dataType: "string", order: 3));

        Synchronize();

        TriggersRefresh("TriggerProbe.json", "Status").Should().BeTrue();
    }

    [Fact]
    public void The_flag_survives_when_the_synchronizer_rewrites_dataType()
    {
        // The discriminator. `Status` is seeded as "int" while the CLR property is a string, so the
        // update branch runs and reassigns DataType. A branch that rebuilt the attribute instead of
        // mutating it would lose the flag here and nowhere else.
        Seed("TriggerProbe.json", MinimalSeed(name: "Status", dataType: "int", order: 3));

        Synchronize();

        Attribute("TriggerProbe.json", "Status").GetProperty("dataType").GetString()
            .Should().Be("string", "the precondition: synchronize must actually have rewritten this attribute");
        TriggersRefresh("TriggerProbe.json", "Status").Should().BeTrue();
    }

    [Fact]
    public void The_flag_survives_when_the_synchronizer_reassigns_order()
    {
        // Order is reassigned only when it is <= 0, which is the second path through the update branch.
        Seed("TriggerProbe.json", MinimalSeed(name: "Status", dataType: "string", order: 0));

        Synchronize();

        Attribute("TriggerProbe.json", "Status").GetProperty("order").GetInt32()
            .Should().BeGreaterThan(0, "the precondition: synchronize must actually have assigned an order");
        TriggersRefresh("TriggerProbe.json", "Status").Should().BeTrue();
    }

    [Fact]
    public void The_flag_reaches_a_fixed_point()
    {
        Seed("TriggerProbe.json", MinimalSeed(name: "Status", dataType: "int", order: 0));

        Synchronize();
        var first = File.ReadAllText(Path.Combine(ModelDir, "TriggerProbe.json"));

        Synchronize();
        Synchronize();

        File.ReadAllText(Path.Combine(ModelDir, "TriggerProbe.json")).Should().Be(first,
            "a preserved field must not churn the file on subsequent runs");
    }

    [Fact]
    public void A_new_attribute_does_not_acquire_the_flag()
    {
        // Absent means absent: the create branch must not materialise `triggersRefresh: false` into
        // every model file in the repository.
        Synchronize();

        Attribute("TriggerProbe.json", "Name").TryGetProperty("triggersRefresh", out _)
            .Should().BeFalse();
    }

    private static string MinimalSeed(string name, string dataType, int order) => $$"""
    {
      "persistentObject": {
        "id": "7a1c9f00-0000-0000-0000-000000000001",
        "name": "TriggerProbe",
        "clrType": "MintPlayer.Spark.Tests.Model.TriggerProbe",
        "attributes": [
          {
            "id": "7a1c9f00-0000-0000-0000-000000000002",
            "name": "{{name}}",
            "dataType": "{{dataType}}",
            "order": {{order}},
            "triggersRefresh": true
          }
        ]
      },
      "queries": []
    }
    """;
}

public sealed class TriggerProbe
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Status { get; set; }
}

public sealed class TriggerContext : SparkContext
{
    public IRavenQueryable<TriggerProbe> TriggerProbes => Session.Query<TriggerProbe>();
}
