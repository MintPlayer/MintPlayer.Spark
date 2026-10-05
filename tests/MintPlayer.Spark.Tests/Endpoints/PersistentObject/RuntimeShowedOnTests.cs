using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents.Linq;
using PO = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// #264 G-Q3/G-Q4: <c>showedOn: None</c> is layout (drawn nowhere), and the per-object answer is the runtime
/// <see cref="PersistentObjectAttribute.ShowedOn"/> an action sets on load and on new — Vidyano's
/// <c>Visibility</c> pattern. Both must reach the wire unchanged: the client draws from them on its first render.
/// </summary>
public class RuntimeShowedOnTests : SparkTestDriver
{
    private static readonly Guid CarTypeId = Guid.Parse("7e5f0000-0000-4000-8000-000000000264");

    private SparkEndpointFactory<ShowedOnProbeContext> _factory = null!;
    private SparkClient _client = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<ShowedOnProbeContext>(Store, [ShowedOnProbeModels.Car(CarTypeId)]);
        _client = new SparkClient(_factory.CreateClient(), ownsClient: true);
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static PersistentObjectAttribute Attribute(PO po, string name) => po.Attributes.Single(a => a.Name == name);

    [Fact]
    public async Task An_attribute_shown_nowhere_still_ships_on_the_object_with_its_value()
    {
        await SeedAsync(s => s.StoreAsync(
            new ShowedOnProbeCar { Status = "InUse", PoliceReport = "PV-1" }, "showedonprobecars/1"));

        var po = await _client.GetPersistentObjectAsync(CarTypeId, "showedonprobecars/1");

        var report = Attribute(po!, "PoliceReport");
        report.ShowedOn.Should().Be(EShowedOn.None, "the model says None and the car is not stolen");
        report.Value?.ToString().Should().Be("PV-1", "None is layout: the value still ships, for custom client code");
    }

    [Fact]
    public async Task A_runtime_ShowedOn_set_in_OnLoad_reaches_the_response()
    {
        await SeedAsync(s => s.StoreAsync(
            new ShowedOnProbeCar { Status = "Stolen", PoliceReport = "PV-2" }, "showedonprobecars/2"));

        var po = await _client.GetPersistentObjectAsync(CarTypeId, "showedonprobecars/2");

        var report = Attribute(po!, "PoliceReport");
        report.ShowedOn.Should().Be(EShowedOn.PersistentObject, "the action shows the field for a stolen car");
        report.IsRequired.Should().BeTrue("the load-time rule travels with the same object");
    }

    [Fact]
    public async Task A_runtime_ShowedOn_set_in_OnNew_reaches_the_new_object()
    {
        var po = await _client.NewPersistentObjectAsync(CarTypeId);

        Attribute(po, "PoliceReport").ShowedOn.Should().Be(EShowedOn.PersistentObject);
    }

    [Fact]
    public async Task A_value_for_an_attribute_shown_nowhere_is_written()
    {
        // Layout is not a write gate (G-Q7): the old IsVisible gate silently dropped exactly this.
        var created = await _client.CreatePersistentObjectAsync(new PO
        {
            Name = "ShowedOnProbeCar",
            ObjectTypeId = CarTypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = "Status", Value = "Stolen" },
                new PersistentObjectAttribute { Name = "PoliceReport", Value = "PV-3" },
            ],
        });

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<ShowedOnProbeCar>(created.Id!)).PoliceReport.Should().Be("PV-3");
    }
}

public sealed class ShowedOnProbeCar
{
    public string? Id { get; set; }
    public string? Status { get; set; }
    public string? PoliceReport { get; set; }
}

public class ShowedOnProbeContext : SparkContext
{
    public IRavenQueryable<ShowedOnProbeCar> ShowedOnProbeCars => Session.Query<ShowedOnProbeCar>();
}

/// <summary>The documented pattern: the model says None, the action shows the field when the state calls for it.</summary>
public class ShowedOnProbeCarActions : DefaultPersistentObjectActions<ShowedOnProbeCar>
{
    public ShowedOnProbeCarActions(MintPlayer.Spark.Services.IEntityMapper mapper) : base(mapper) { }

    public override async Task<PO?> OnLoadAsync(string id, PO? parent)
    {
        var po = await base.OnLoadAsync(id, parent);
        if (po is not null)
        {
            var stolen = po["Status"].Value?.ToString() == "Stolen";
            po["PoliceReport"].ShowedOn = stolen ? EShowedOn.PersistentObject : EShowedOn.None;
            po["PoliceReport"].IsRequired = stolen;
        }
        return po;
    }

    public override Task OnNewAsync(SparkNewArgs<ShowedOnProbeCar> args)
    {
        // A new report is filed for a stolen car, so the field starts shown.
        args.PersistentObject["PoliceReport"].ShowedOn = EShowedOn.PersistentObject;
        return base.OnNewAsync(args);
    }
}

public static class ShowedOnProbeModels
{
    public static EntityTypeFile Car(Guid id) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = "ShowedOnProbeCar",
            ClrType = typeof(ShowedOnProbeCar).FullName!,
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Status", DataType = "string", Order = 1 },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "PoliceReport", DataType = "string", Order = 2,
                    ShowedOn = EShowedOn.None,
                },
            ],
        }
    };
}
