using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Integration coverage for <see cref="BreadcrumbResolver"/> against embedded RavenDB. Pins the
/// core guarantees: recursive resolution across references, O(depth) batched loading (request-count
/// assertions), reference-array joining, row-level redaction, the projection-only-root fallback,
/// and cycle termination.
/// </summary>
public class BreadcrumbResolverTests : SparkTestDriver
{
    // --- entities ---
    public class BR_Person { public string? Id { get; set; } public string FirstName { get; set; } = ""; public string LastName { get; set; } = ""; public string? Manager { get; set; } }
    public class BR_Car { public string? Id { get; set; } public string LicensePlate { get; set; } = ""; public string? Driver { get; set; } }
    public class BR_ParkingSpot { public string? Id { get; set; } public string Coordinates { get; set; } = ""; public string? ParkedCar { get; set; } }
    public class BR_Post { public string? Id { get; set; } public string Title { get; set; } = ""; public List<string> TagIds { get; set; } = []; }
    public class BR_Tag { public string? Id { get; set; } public string Name { get; set; } = ""; }
    public class BR_VPerson { public string? Id { get; set; } public string FullName { get; set; } = ""; }

    // --- model builders ---
    private static EntityAttributeDefinition Scalar(string name) =>
        new() { Id = Guid.NewGuid(), Name = name, DataType = "string" };
    private static EntityAttributeDefinition Ref(string name, Type target, bool isArray = false) =>
        new() { Id = Guid.NewGuid(), Name = name, DataType = "Reference", ReferenceType = target.FullName, IsArray = isArray };
    private static EntityTypeDefinition Def(Type clr, string breadcrumb, bool? satisfiable, params EntityAttributeDefinition[] attrs) =>
        new() { Id = Guid.NewGuid(), Name = clr.Name, ClrType = clr.FullName!, Breadcrumb = breadcrumb, BreadcrumbProjectionSatisfiable = satisfiable, Attributes = attrs };

    private static (IBreadcrumbResolver resolver, IRowSecurity rowSecurity) Build(params EntityTypeDefinition[] defs)
    {
        var loader = Substitute.For<IModelLoader>();
        loader.GetEntityTypes().Returns(defs);
        var byClr = defs.ToDictionary(d => d.ClrType, d => d, StringComparer.Ordinal);
        loader.GetEntityTypeByClrType(Arg.Any<string>()).Returns(ci => byClr.GetValueOrDefault((string)ci[0]!));

        var rowSecurity = Substitute.For<IRowSecurity>();
        rowSecurity.IsAllowedAsync(default!, default!, default!).ReturnsForAnyArgs(true);

        var closure = new BreadcrumbClosure(loader);
        var resolver = new BreadcrumbResolver(loader, closure, rowSecurity, new SparkOptions());
        return (resolver, rowSecurity);
    }

    // ParkingSpot -> Car -> Person, a 3-level chain.
    private (EntityTypeDefinition person, EntityTypeDefinition car, EntityTypeDefinition spot) ChainDefs()
    {
        var person = Def(typeof(BR_Person), "{FirstName} {LastName}", null, Scalar("FirstName"), Scalar("LastName"));
        var car = Def(typeof(BR_Car), "{LicensePlate} ({Driver})", null, Scalar("LicensePlate"), Ref("Driver", typeof(BR_Person)));
        var spot = Def(typeof(BR_ParkingSpot), "{ParkedCar} ({Coordinates})", null, Scalar("Coordinates"), Ref("ParkedCar", typeof(BR_Car)));
        return (person, car, spot);
    }

    [Fact]
    public async Task Empty_roots_returns_empty()
    {
        var (person, car, spot) = ChainDefs();
        var (resolver, _) = Build(person, car, spot);
        using var session = Store.OpenAsyncSession();

        var result = await resolver.ResolveAsync(session, [], spot);

        result.BreadcrumbsById.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public async Task Three_level_chain_renders_recursively_in_O_depth_requests(int n)
    {
        using (var seed = Store.OpenAsyncSession())
        {
            for (var i = 0; i < n; i++)
            {
                await seed.StoreAsync(new BR_Person { FirstName = $"P{i}", LastName = "X" }, $"people/{i}");
                await seed.StoreAsync(new BR_Car { LicensePlate = $"CAR-{i}", Driver = $"people/{i}" }, $"cars/{i}");
                await seed.StoreAsync(new BR_ParkingSpot { Coordinates = $"{i},{i}", ParkedCar = $"cars/{i}" }, $"spots/{i}");
            }
            await seed.SaveChangesAsync();
        }

        var (person, car, spot) = ChainDefs();
        var (resolver, _) = Build(person, car, spot);
        using var session = Store.OpenAsyncSession();
        var spotIds = Enumerable.Range(0, n).Select(i => $"spots/{i}").ToList();
        var spots = (await session.LoadAsync<BR_ParkingSpot>(spotIds)).Values.Where(s => s is not null).Cast<object>().ToList();

        var before = session.Advanced.NumberOfRequests;
        var result = await resolver.ResolveAsync(session, spots, spot);
        var added = session.Advanced.NumberOfRequests - before;

        added.Should().Be(2, $"cost is O(depth): one batched load for cars, one for people — independent of n={n}");
        result.Get("spots/0").Should().Be("CAR-0 (P0 X) (0,0)");
        result.Get($"spots/{n - 1}").Should().Be($"CAR-{n - 1} (P{n - 1} X) ({n - 1},{n - 1})");
        result.Get("cars/0").Should().Be("CAR-0 (P0 X)");
        result.Get("people/0").Should().Be("P0 X");
    }

    [Fact]
    public async Task Root_reference_attribute_outside_the_breadcrumb_template_is_still_resolved()
    {
        // Car's breadcrumb is just {LicensePlate} — Driver is NOT in the template, but the Driver
        // column still needs a display label, so the resolver must resolve it for the root.
        using (var seed = Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new BR_Person { FirstName = "John", LastName = "Doe" }, "people/1");
            await seed.StoreAsync(new BR_Car { LicensePlate = "CAR-1", Driver = "people/1" }, "cars/1");
            await seed.SaveChangesAsync();
        }

        var person = Def(typeof(BR_Person), "{FirstName} {LastName}", null, Scalar("FirstName"), Scalar("LastName"));
        var car = Def(typeof(BR_Car), "{LicensePlate}", null, Scalar("LicensePlate"), Ref("Driver", typeof(BR_Person)));
        var (resolver, _) = Build(person, car);

        using var session = Store.OpenAsyncSession();
        var carEntity = await session.LoadAsync<BR_Car>("cars/1");

        var result = await resolver.ResolveAsync(session, [carEntity], car);

        result.Get("cars/1").Should().Be("CAR-1", "the car's own breadcrumb omits the driver");
        result.Get("people/1").Should().Be("John Doe", "but the driver reference still resolves a label");
    }

    [Fact]
    public async Task Reference_array_joins_each_referenced_breadcrumb()
    {
        using (var seed = Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new BR_Tag { Name = "news" }, "tags/1");
            await seed.StoreAsync(new BR_Tag { Name = "sports" }, "tags/2");
            await seed.StoreAsync(new BR_Post { Title = "Post", TagIds = ["tags/1", "tags/2"] }, "posts/1");
            await seed.SaveChangesAsync();
        }

        var tag = Def(typeof(BR_Tag), "{Name}", null, Scalar("Name"));
        var post = Def(typeof(BR_Post), "{Title}: {TagIds}", null, Scalar("Title"), Ref("TagIds", typeof(BR_Tag), isArray: true));
        var (resolver, _) = Build(tag, post);

        using var session = Store.OpenAsyncSession();
        var postEntity = await session.LoadAsync<BR_Post>("posts/1");

        var result = await resolver.ResolveAsync(session, [postEntity], post);

        result.Get("posts/1").Should().Be("Post: news, sports");
    }

    [Fact]
    public async Task Denied_reference_renders_the_redacted_placeholder()
    {
        using (var seed = Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new BR_Person { FirstName = "Secret", LastName = "Agent" }, "people/1");
            await seed.StoreAsync(new BR_Car { LicensePlate = "CAR-1", Driver = "people/1" }, "cars/1");
            await seed.SaveChangesAsync();
        }

        var person = Def(typeof(BR_Person), "{FirstName} {LastName}", null, Scalar("FirstName"), Scalar("LastName"));
        var car = Def(typeof(BR_Car), "{LicensePlate} ({Driver})", null, Scalar("LicensePlate"), Ref("Driver", typeof(BR_Person)));
        var (resolver, rowSecurity) = Build(person, car);
        // Deny row-level Read on the Person behind the wheel.
        rowSecurity.IsAllowedAsync(typeof(BR_Person), "Read", Arg.Any<object>()).Returns(false);

        using var session = Store.OpenAsyncSession();
        var carEntity = await session.LoadAsync<BR_Car>("cars/1");

        var result = await resolver.ResolveAsync(session, [carEntity], car);

        result.Get("cars/1").Should().Be("CAR-1 (—)", "the denied driver is redacted, the rest renders");
        result.Get("people/1").Should().Be("—");
    }

    [Fact]
    public async Task Projection_only_root_loads_the_collection_document_to_render_collection_fields()
    {
        using (var seed = Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new BR_Person { FirstName = "Ada", LastName = "Lovelace" }, "people/1");
            await seed.SaveChangesAsync();
        }

        // Person's breadcrumb uses FirstName/LastName, which the VPerson projection lacks → not satisfiable.
        var person = Def(typeof(BR_Person), "{FirstName} {LastName}", satisfiable: false, Scalar("FirstName"), Scalar("LastName"));
        var (resolver, _) = Build(person);

        using var session = Store.OpenAsyncSession();
        // Roots are projection instances WITHOUT the collection fields.
        var projection = new BR_VPerson { Id = "people/1", FullName = "ignored" };

        var before = session.Advanced.NumberOfRequests;
        var result = await resolver.ResolveAsync(session, [projection], person);
        var added = session.Advanced.NumberOfRequests - before;

        added.Should().Be(1, "one batched load fetches the collection documents for the page");
        result.Get("people/1").Should().Be("Ada Lovelace");
    }

    [Fact]
    public async Task Reference_cycle_terminates()
    {
        using (var seed = Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new BR_Person { FirstName = "A", LastName = "Alpha", Manager = "people/2" }, "people/1");
            await seed.StoreAsync(new BR_Person { FirstName = "B", LastName = "Beta", Manager = "people/1" }, "people/2");
            await seed.SaveChangesAsync();
        }

        var person = Def(typeof(BR_Person), "{LastName} (mgr: {Manager})", null, Scalar("LastName"), Ref("Manager", typeof(BR_Person)));
        var (resolver, _) = Build(person);

        using var session = Store.OpenAsyncSession();
        var a = await session.LoadAsync<BR_Person>("people/1");

        var result = await resolver.ResolveAsync(session, [a], person);

        // Must terminate (no stack overflow / hang) and name both people before the cycle is cut.
        var crumb = result.Get("people/1");
        crumb.Should().NotBeNullOrEmpty();
        crumb.Should().StartWith("Alpha (mgr: Beta (mgr: ");
    }
}
