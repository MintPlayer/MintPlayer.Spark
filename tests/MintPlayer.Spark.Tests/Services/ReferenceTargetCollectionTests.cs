using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client;
using Raven.Client.Documents.Linq;
using System.Text.Json;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #251, the write half: every reference id a save newly writes must name a document in the collection
/// its <c>[Reference]</c> declares (<see cref="ReferenceTargetValidation"/>), through the real
/// <see cref="IDatabaseAccess"/> pipeline.
/// <para>
/// The first half pins that <b>legitimate saves keep working</b> — every shape of reference value a
/// save writes, and every kind of valid target, including the ones a naive "id prefix" or "typed
/// load" check would refuse. The second half pins the refusals.
/// </para>
/// </summary>
public class ReferenceTargetCollectionTests : SparkTestDriver
{
    private static readonly Guid CarTypeId = Guid.Parse("251c0000-0000-4000-8000-251c00000001");
    private static readonly Guid PersonTypeId = Guid.Parse("251c0000-0000-4000-8000-251c00000002");
    private static readonly Guid StopTypeId = Guid.Parse("251c0000-0000-4000-8000-251c00000003");

    private SparkEndpointFactory<RtContext> _factory = null!;

    static ReferenceTargetCollectionTests()
        => SparkValueObjects.Register(typeof(RT_Badge), nameof(RT_Badge.Key), row => ((RT_Badge)row).Key);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<RtContext>(Store, [PersonModel(), StopModel(), CarModel()]);
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>One save = one scope = one session, the way a request works.</summary>
    private async Task<PersistentObject> SaveAsync(PersistentObject po)
    {
        using var scope = _factory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IDatabaseAccess>().SavePersistentObjectAsync(po);
    }

    private async Task SeedAsync(params (object Entity, string Id)[] docs)
    {
        using var session = Store.OpenAsyncSession();
        foreach (var (entity, id) in docs)
            await session.StoreAsync(entity, id);
        await session.SaveChangesAsync();
    }

    private async Task<RT_Car?> LoadCarAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<RT_Car>(id);
    }

    private static PersistentObject Car(string? id = null, string plate = "CAR-1", object? driver = null,
        object? passengers = null, PersistentObject[]? stops = null, string? badge = null)
    {
        var po = new PersistentObject { Id = id, ObjectTypeId = CarTypeId, Name = "RT_Car" };
        po.AddAttribute(new PersistentObjectAttribute { Name = "Plate", DataType = "string", Value = plate, IsValueChanged = true });
        if (driver is not null)
            po.AddAttribute(new PersistentObjectAttribute { Name = "Driver", DataType = "Reference", Value = driver, IsValueChanged = true });
        if (passengers is not null)
            po.AddAttribute(new PersistentObjectAttribute { Name = "Passengers", DataType = "Reference", IsArray = true, Value = passengers, IsValueChanged = true });
        if (stops is not null)
            po.AddAttribute(new PersistentObjectAttributeAsDetail
            {
                Name = "Stops", DataType = "AsDetail", IsArray = true, AsDetailType = typeof(RT_Stop).FullName,
                Objects = stops, IsValueChanged = true,
            });
        if (badge is not null)
            po.AddAttribute(new PersistentObjectAttribute { Name = "Badge", DataType = "Reference", Value = badge, IsValueChanged = true });
        return po;
    }

    private static PersistentObject Stop(string contact)
    {
        var po = new PersistentObject { Id = string.Empty, ObjectTypeId = StopTypeId, Name = "RT_Stop" };
        po.AddAttribute(new PersistentObjectAttribute { Name = "Contact", DataType = "Reference", Value = contact, IsValueChanged = true });
        return po;
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    // --- legitimate saves keep working -----------------------------------------------------------

    [Fact]
    public async Task A_reference_to_a_document_of_the_target_type_saves()
    {
        await SeedAsync((new RT_Person { Name = "Ada" }, "rt_people/1"));

        var saved = await SaveAsync(Car(driver: "rt_people/1"));

        (await LoadCarAsync(saved.Id!))!.Driver.Should().Be("rt_people/1");
    }

    [Fact]
    public async Task A_reference_id_as_the_HTTP_body_carries_it_saves()
    {
        await SeedAsync((new RT_Person { Name = "Ada" }, "rt_people/1"), (new RT_Person { Name = "Bob" }, "rt_people/2"));

        // The endpoints hand the pipeline JsonElements, not CLR strings and lists.
        var saved = await SaveAsync(Car(driver: Json("rt_people/1"), passengers: Json(new[] { "rt_people/1", "rt_people/2" })));

        var car = await LoadCarAsync(saved.Id!);
        car!.Driver.Should().Be("rt_people/1");
        car.Passengers.Should().Equal("rt_people/1", "rt_people/2");
    }

    [Fact]
    public async Task A_reference_whose_id_prefix_differs_from_its_collection_saves()
    {
        // The collection is the stored type's, not the id prefix: natural and custom ids are legitimate.
        await SeedAsync((new RT_Person { Name = "Ada" }, "anything/ada"));

        var saved = await SaveAsync(Car(driver: "anything/ada"));

        (await LoadCarAsync(saved.Id!))!.Driver.Should().Be("anything/ada");
    }

    [Fact]
    public async Task A_reference_to_a_subtype_stored_in_its_own_collection_saves()
    {
        await SeedAsync((new RT_Chauffeur { Name = "Grace", Licence = "B" }, "rt_chauffeurs/1"));

        var saved = await SaveAsync(Car(driver: "rt_chauffeurs/1"));

        (await LoadCarAsync(saved.Id!))!.Driver.Should().Be("rt_chauffeurs/1");
    }

    [Fact]
    public async Task A_reference_to_a_document_whose_stored_clr_type_no_longer_resolves_saves()
    {
        // Coverage's pre-monorepo documents: in the right collection, under a type name that no longer exists.
        using (var seed = Store.OpenAsyncSession())
        {
            var person = new RT_Person { Name = "Legacy" };
            await seed.StoreAsync(person, "rt_people/legacy");
            seed.Advanced.GetMetadataFor(person)[Constants.Documents.Metadata.RavenClrType] = "Gone.Namespace.Person, Gone.Assembly";
            await seed.SaveChangesAsync();
        }

        var saved = await SaveAsync(Car(driver: "rt_people/legacy"));

        (await LoadCarAsync(saved.Id!))!.Driver.Should().Be("rt_people/legacy");
    }

    [Fact]
    public async Task A_reference_array_of_target_documents_saves()
    {
        await SeedAsync((new RT_Person { Name = "Ada" }, "rt_people/1"), (new RT_Person { Name = "Bob" }, "rt_people/2"));

        var saved = await SaveAsync(Car(passengers: new List<string> { "rt_people/1", "rt_people/2" }));

        (await LoadCarAsync(saved.Id!))!.Passengers.Should().Equal("rt_people/1", "rt_people/2");
    }

    [Fact]
    public async Task A_reference_inside_an_AsDetail_row_to_a_target_document_saves()
    {
        await SeedAsync((new RT_Person { Name = "Ada" }, "rt_people/1"));

        var saved = await SaveAsync(Car(stops: [Stop("rt_people/1")]));

        (await LoadCarAsync(saved.Id!))!.Stops.Select(s => s.Contact).Should().Equal("rt_people/1");
    }

    [Fact]
    public async Task A_reference_to_a_value_object_saves_although_no_document_exists()
    {
        // A [ValueObject] has no documents of its own (reference add-new PRD D10): its key lives only in
        // the owner's value, so there is nothing to look up.
        var saved = await SaveAsync(Car(badge: "gold"));

        (await LoadCarAsync(saved.Id!))!.Badge.Should().Be("gold");
    }

    [Fact]
    public async Task A_dangling_id_the_row_already_holds_does_not_block_an_edit()
    {
        // Stored before the check existed; the target is gone. The caller only changes the plate.
        await SeedAsync((new RT_Car { Plate = "OLD", Driver = "rt_people/deleted", Passengers = ["rt_people/deleted"] }, "rt_cars/1"));

        await SaveAsync(Car(id: "rt_cars/1", plate: "NEW", driver: "rt_people/deleted", passengers: new List<string> { "rt_people/deleted" }));

        var car = await LoadCarAsync("rt_cars/1");
        car!.Plate.Should().Be("NEW");
        car.Driver.Should().Be("rt_people/deleted");
    }

    [Fact]
    public async Task A_reference_to_a_document_this_request_stored_but_has_not_committed_saves()
    {
        // An Actions class (or a write batch) that creates the person and then the car in one request:
        // the person exists only in the request session until the car's save commits both.
        using var scope = _factory.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<Raven.Client.Documents.Session.IAsyncDocumentSession>();
        await session.StoreAsync(new RT_Person { Name = "New" }, "rt_people/new");

        var saved = await scope.ServiceProvider.GetRequiredService<IDatabaseAccess>().SavePersistentObjectAsync(Car(driver: "rt_people/new"));

        (await LoadCarAsync(saved.Id!))!.Driver.Should().Be("rt_people/new");
    }

    [Fact]
    public async Task Clearing_a_reference_saves()
    {
        await SeedAsync((new RT_Car { Plate = "CAR-1", Driver = "rt_people/deleted" }, "rt_cars/1"));

        var po = Car(id: "rt_cars/1");
        po.AddAttribute(new PersistentObjectAttribute { Name = "Driver", DataType = "Reference", Value = null, IsValueChanged = true });
        await SaveAsync(po);

        (await LoadCarAsync("rt_cars/1"))!.Driver.Should().BeNull();
    }

    // --- refusals ----------------------------------------------------------------------------------

    private async Task<ValidationError> RefusedAsync(PersistentObject po)
    {
        var act = () => SaveAsync(po);
        var thrown = await act.Should().ThrowAsync<SparkSaveValidationException>();
        return thrown.Which.Result.Errors.Should().ContainSingle().Which;
    }

    [Fact]
    public async Task A_reference_to_a_document_of_another_collection_is_refused_and_nothing_is_written()
    {
        // Field-compatible with RT_Person, so a typed load would have deserialized it without complaint.
        await SeedAsync((new RT_Account { Name = "Secret Owner" }, "rt_accounts/1"));

        var error = await RefusedAsync(Car(driver: "rt_accounts/1"));

        error.AttributeName.Should().Be("Driver");
        error.RuleType.Should().Be("referenceTarget");
        using var session = Store.OpenAsyncSession();
        (await session.Advanced.AsyncDocumentQuery<RT_Car>().WaitForNonStaleResults().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_missing_target_is_refused_with_the_same_message_as_a_foreign_one()
    {
        await SeedAsync((new RT_Account { Name = "Secret Owner" }, "rt_accounts/1"));

        var foreign = await RefusedAsync(Car(driver: "rt_accounts/1"));
        var missing = await RefusedAsync(Car(driver: "rt_accounts/does-not-exist"));

        // One answer for both, so a save is no oracle for whether some document id exists.
        missing.ErrorMessage.Translations.Should().BeEquivalentTo(foreign.ErrorMessage.Translations);
        missing.ErrorMessage.Translations.Values.Should().NotContain(t => t.Contains("rt_accounts"));
    }

    [Fact]
    public async Task A_foreign_id_in_a_reference_array_as_JSON_is_refused()
    {
        await SeedAsync((new RT_Person { Name = "Ada" }, "rt_people/1"), (new RT_Account { Name = "Secret" }, "rt_accounts/1"));

        var error = await RefusedAsync(Car(passengers: Json(new[] { "rt_people/1", "rt_accounts/1" })));

        error.AttributeName.Should().Be("Passengers");
    }

    [Fact]
    public async Task A_foreign_id_inside_an_AsDetail_row_is_refused()
    {
        await SeedAsync((new RT_Account { Name = "Secret" }, "rt_accounts/1"));

        var error = await RefusedAsync(Car(stops: [Stop("rt_accounts/1")]));

        error.AttributeName.Should().Be("Stops");
    }

    [Fact]
    public async Task Changing_a_kept_reference_to_a_foreign_id_is_refused_and_the_row_is_untouched()
    {
        await SeedAsync((new RT_Person { Name = "Ada" }, "rt_people/1"), (new RT_Account { Name = "Secret" }, "rt_accounts/1"),
            (new RT_Car { Plate = "CAR-1", Driver = "rt_people/1" }, "rt_cars/1"));

        var error = await RefusedAsync(Car(id: "rt_cars/1", driver: "rt_accounts/1"));

        error.AttributeName.Should().Be("Driver");
        (await LoadCarAsync("rt_cars/1"))!.Driver.Should().Be("rt_people/1");
    }

    // --- model -------------------------------------------------------------------------------------

    private static EntityAttributeDefinition Attr(string name, string dataType = "string") =>
        new() { Id = Guid.NewGuid(), Name = name, DataType = dataType };

    private static EntityAttributeDefinition Ref(string name, Type target, bool isArray = false) =>
        new() { Id = Guid.NewGuid(), Name = name, DataType = "Reference", ReferenceType = target.FullName, IsArray = isArray, Query = "GetRtPeople" };

    private static EntityTypeFile PersonModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = PersonTypeId, Name = "RT_Person", ClrType = typeof(RT_Person).FullName!, Breadcrumb = "{Name}",
            Attributes = [Attr("Name")],
        },
    };

    private static EntityTypeFile StopModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = StopTypeId, Name = "RT_Stop", ClrType = typeof(RT_Stop).FullName!,
            Attributes = [Ref("Contact", typeof(RT_Person))],
        },
    };

    private static EntityTypeFile CarModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = CarTypeId, Name = "RT_Car", ClrType = typeof(RT_Car).FullName!, Breadcrumb = "{Plate}",
            Attributes =
            [
                Attr("Plate"),
                Ref("Driver", typeof(RT_Person)),
                Ref("Passengers", typeof(RT_Person), isArray: true),
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Stops", DataType = "AsDetail", AsDetailType = typeof(RT_Stop).FullName, IsArray = true,
                },
                Ref("Badge", typeof(RT_Badge)),
            ],
        },
    };
}

public class RT_Person
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>A subtype of <see cref="RT_Person"/>; RavenDB stores it in its own collection.</summary>
public class RT_Chauffeur : RT_Person
{
    public string Licence { get; set; } = string.Empty;
}

/// <summary>Not in the model, and field-compatible with <see cref="RT_Person"/>.</summary>
public class RT_Account
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>A value object: no documents of its own.</summary>
public class RT_Badge
{
    public string Key { get; set; } = string.Empty;
}

public class RT_Stop
{
    [Reference(typeof(RT_Person))]
    public string? Contact { get; set; }
}

public class RT_Car
{
    public string? Id { get; set; }
    public string Plate { get; set; } = string.Empty;

    [Reference(typeof(RT_Person))]
    public string? Driver { get; set; }

    [Reference(typeof(RT_Person))]
    public List<string> Passengers { get; set; } = [];

    public List<RT_Stop> Stops { get; set; } = [];

    [Reference(typeof(RT_Badge))]
    public string? Badge { get; set; }
}

public class RT_PersonActions : DefaultPersistentObjectActions<RT_Person>
{
    public RT_PersonActions(IEntityMapper entityMapper) : base(entityMapper) { }
}

public class RT_CarActions : DefaultPersistentObjectActions<RT_Car>
{
    public RT_CarActions(IEntityMapper entityMapper) : base(entityMapper) { }
}

public class RtContext : SparkContext
{
    public IQueryable<RT_Person> RtPeople => Session.Query<RT_Person>();
    public IQueryable<RT_Car> RtCars => Session.Query<RT_Car>();
}
