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
    // AsDetail shapes (#185): a collection root with an embedded array whose children reference a collection.
    public class BR_Artist { public string? Id { get; set; } public string Name { get; set; } = ""; }
    public class BR_SongArtist { public string? ArtistId { get; set; } }
    public class BR_Song { public string? Id { get; set; } public string Title { get; set; } = ""; public List<BR_SongArtist> Artists { get; set; } = []; }
    // Nested AsDetail-within-AsDetail: Person -> Jobs[] -> Certifications[] -> Issuer (a collection).
    public class BR_Issuer { public string? Id { get; set; } public string Name { get; set; } = ""; }
    public class BR_Certification { public string? IssuerId { get; set; } }
    public class BR_Job { public List<BR_Certification> Certifications { get; set; } = []; }
    public class BR_Employee { public string? Id { get; set; } public string Name { get; set; } = ""; public List<BR_Job> Jobs { get; set; } = []; }
    // An AsDetail-nested reference whose target's OWN breadcrumb itself contains a reference.
    public class BR_Country { public string? Id { get; set; } public string Name { get; set; } = ""; }
    public class BR_Label { public string? Id { get; set; } public string Name { get; set; } = ""; public string? CountryId { get; set; } }
    public class BR_Credit { public string? LabelId { get; set; } }
    public class BR_Release { public string? Id { get; set; } public string Title { get; set; } = ""; public List<BR_Credit> Credits { get; set; } = []; }

    // --- model builders ---
    private static EntityAttributeDefinition Scalar(string name) =>
        new() { Id = Guid.NewGuid(), Name = name, DataType = "string" };
    private static EntityAttributeDefinition Ref(string name, Type target, bool isArray = false) =>
        new() { Id = Guid.NewGuid(), Name = name, DataType = "Reference", ReferenceType = target.FullName, IsArray = isArray };
    private static EntityTypeDefinition Def(Type clr, string breadcrumb, bool? satisfiable, params EntityAttributeDefinition[] attrs) =>
        new() { Id = Guid.NewGuid(), Name = clr.Name, ClrType = clr.FullName!, Breadcrumb = breadcrumb, BreadcrumbProjectionSatisfiable = satisfiable, Attributes = attrs };
    private static EntityAttributeDefinition AsDetailArr(string name, Type child) =>
        new() { Id = Guid.NewGuid(), Name = name, DataType = "AsDetail", AsDetailType = child.FullName, IsArray = true };

    private static (IBreadcrumbResolver resolver, IRowSecurity rowSecurity) Build(params EntityTypeDefinition[] defs)
        => Build(new SparkOptions(), defs);

    private static (IBreadcrumbResolver resolver, IRowSecurity rowSecurity) Build(SparkOptions options, params EntityTypeDefinition[] defs)
    {
        var loader = Substitute.For<IModelLoader>();
        loader.GetEntityTypes().Returns(defs);
        var byClr = defs.ToDictionary(d => d.ClrType, d => d, StringComparer.Ordinal);
        loader.GetEntityTypeByClrType(Arg.Any<string>()).Returns(ci => byClr.GetValueOrDefault((string)ci[0]!));

        var rowSecurity = Substitute.For<IRowSecurity>();
        rowSecurity.IsAllowedAsync(default!, default!, default!).ReturnsForAnyArgs(true);

        var closure = new BreadcrumbClosure(loader);
        var resolver = new BreadcrumbResolver(loader, closure, rowSecurity, options);
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

    [Fact]
    public async Task Null_reference_field_renders_nothing_for_that_placeholder()
    {
        // Car with no Driver assigned — the {Driver} placeholder contributes the empty string,
        // and the resolver must not crash trying to load a null id.
        using (var seed = Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new BR_Car { LicensePlate = "CAR-1", Driver = null }, "cars/1");
            await seed.SaveChangesAsync();
        }

        var person = Def(typeof(BR_Person), "{FirstName} {LastName}", null, Scalar("FirstName"), Scalar("LastName"));
        var car = Def(typeof(BR_Car), "{LicensePlate} ({Driver})", null, Scalar("LicensePlate"), Ref("Driver", typeof(BR_Person)));
        var (resolver, _) = Build(person, car);

        using var session = Store.OpenAsyncSession();
        var carEntity = await session.LoadAsync<BR_Car>("cars/1");

        var result = await resolver.ResolveAsync(session, [carEntity], car);

        result.Get("cars/1").Should().Be("CAR-1 ()", "a null reference contributes no text");
    }

    [Fact]
    public async Task Empty_reference_array_joins_to_nothing()
    {
        using (var seed = Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new BR_Post { Title = "Untagged", TagIds = [] }, "posts/1");
            await seed.SaveChangesAsync();
        }

        var tag = Def(typeof(BR_Tag), "{Name}", null, Scalar("Name"));
        var post = Def(typeof(BR_Post), "{Title}: {TagIds}", null, Scalar("Title"), Ref("TagIds", typeof(BR_Tag), isArray: true));
        var (resolver, _) = Build(tag, post);

        using var session = Store.OpenAsyncSession();
        var postEntity = await session.LoadAsync<BR_Post>("posts/1");

        var result = await resolver.ResolveAsync(session, [postEntity], post);

        result.Get("posts/1").Should().Be("Untagged: ");
    }

    [Fact]
    public async Task Chain_deeper_than_MaxDepth_is_truncated_at_the_unloaded_level()
    {
        // ParkingSpot -> Car -> Person, but MaxDepth caps the resolver before Person is loaded.
        using (var seed = Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new BR_Person { FirstName = "John", LastName = "Doe" }, "people/1");
            await seed.StoreAsync(new BR_Car { LicensePlate = "CAR-1", Driver = "people/1" }, "cars/1");
            await seed.StoreAsync(new BR_ParkingSpot { Coordinates = "1,1", ParkedCar = "cars/1" }, "spots/1");
            await seed.SaveChangesAsync();
        }

        var (person, car, spot) = ChainDefs();
        // MaxDepth = 2: the loop runs once (loads cars) then stops before loading people.
        var options = new SparkOptions { Breadcrumb = new() { MaxDepth = 2 } };
        var (resolver, _) = Build(options, person, car, spot);

        using var session = Store.OpenAsyncSession();
        var spotEntity = await session.LoadAsync<BR_ParkingSpot>("spots/1");

        var before = session.Advanced.NumberOfRequests;
        var result = await resolver.ResolveAsync(session, [spotEntity], spot);
        var added = session.Advanced.NumberOfRequests - before;

        added.Should().Be(1, "only one reference level is loaded before MaxDepth caps the walk");
        // Car loaded, Person not: the {Driver} placeholder renders empty (unloaded), the rest renders.
        result.Get("spots/1").Should().Be("CAR-1 () (1,1)");
        result.Get("cars/1").Should().Be("CAR-1 ()");
        result.Get("people/1").Should().BeNull("person was never loaded, so it has no breadcrumb");
    }

    [Fact]
    public async Task Self_referencing_document_renders_without_infinite_recursion()
    {
        // A single document whose Manager points at itself — the per-path visited set cuts the loop.
        using (var seed = Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new BR_Person { FirstName = "Solo", LastName = "Self", Manager = "people/1" }, "people/1");
            await seed.SaveChangesAsync();
        }

        var person = Def(typeof(BR_Person), "{LastName} (mgr: {Manager})", null, Scalar("LastName"), Ref("Manager", typeof(BR_Person)));
        var (resolver, _) = Build(person);

        using var session = Store.OpenAsyncSession();
        var self = await session.LoadAsync<BR_Person>("people/1");

        var result = await resolver.ResolveAsync(session, [self], person);

        var crumb = result.Get("people/1");
        crumb.Should().NotBeNullOrEmpty();
        // First level expands the self-reference once; the re-entry suppresses further expansion
        // (the inner {Manager} contributes empty), so we terminate.
        crumb.Should().StartWith("Self (mgr: Self (mgr: ");
    }

    // --- #185: references nested inside embedded AsDetail children of the roots ---

    [Fact]
    public async Task AsDetail_child_references_are_resolved_for_roots_regardless_of_options_page()
    {
        // A Song with an AsDetail array of credited artists; each credit references an Artist.
        // The resolver loads referenced docs BY ID, so page membership of any options query is
        // irrelevant — every credited artist must resolve, including ones that would sort onto a
        // later options page (the #185 repro: Songs/43 credits artists/40, /41, /42).
        using (var seed = Store.OpenAsyncSession())
        {
            for (var i = 0; i < 50; i++)
                await seed.StoreAsync(new BR_Artist { Name = $"Artist{i}" }, $"artists/{i}");
            await seed.StoreAsync(new BR_Song
            {
                Title = "1-800-273-8255",
                Artists =
                [
                    new BR_SongArtist { ArtistId = "artists/40" },
                    new BR_SongArtist { ArtistId = "artists/41" },
                    new BR_SongArtist { ArtistId = "artists/42" },
                ],
            }, "songs/43");
            await seed.SaveChangesAsync();
        }

        var artist = Def(typeof(BR_Artist), "{Name}", null, Scalar("Name"));
        var songArtist = Def(typeof(BR_SongArtist), "{ArtistId}", null, Ref("ArtistId", typeof(BR_Artist)));
        var song = Def(typeof(BR_Song), "{Title}", null, Scalar("Title"), AsDetailArr("Artists", typeof(BR_SongArtist)));
        var (resolver, _) = Build(artist, songArtist, song);

        using var session = Store.OpenAsyncSession();
        var songEntity = await session.LoadAsync<BR_Song>("songs/43");

        var before = session.Advanced.NumberOfRequests;
        var result = await resolver.ResolveAsync(session, [songEntity], song);
        var added = session.Advanced.NumberOfRequests - before;

        result.Get("artists/40").Should().Be("Artist40");
        result.Get("artists/41").Should().Be("Artist41");
        result.Get("artists/42").Should().Be("Artist42");
        added.Should().Be(1, "the AsDetail-nested references load in one batched level — O(depth), not O(rows)");
    }

    [Fact]
    public async Task AsDetail_descent_is_recursive_through_nested_AsDetail()
    {
        // Employee -> Jobs[] (AsDetail) -> Certifications[] (AsDetail) -> IssuerId (Reference).
        // The deepest reference, two AsDetail levels down, must still resolve.
        using (var seed = Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new BR_Issuer { Name = "Microsoft" }, "issuers/1");
            await seed.StoreAsync(new BR_Issuer { Name = "Google" }, "issuers/2");
            await seed.StoreAsync(new BR_Employee
            {
                Name = "Ada",
                Jobs =
                [
                    new BR_Job { Certifications = [new BR_Certification { IssuerId = "issuers/1" }] },
                    new BR_Job { Certifications = [new BR_Certification { IssuerId = "issuers/2" }] },
                ],
            }, "employees/1");
            await seed.SaveChangesAsync();
        }

        var issuer = Def(typeof(BR_Issuer), "{Name}", null, Scalar("Name"));
        var cert = Def(typeof(BR_Certification), "{IssuerId}", null, Ref("IssuerId", typeof(BR_Issuer)));
        var job = Def(typeof(BR_Job), "", null, AsDetailArr("Certifications", typeof(BR_Certification)));
        var employee = Def(typeof(BR_Employee), "{Name}", null, Scalar("Name"), AsDetailArr("Jobs", typeof(BR_Job)));
        var (resolver, _) = Build(issuer, cert, job, employee);

        using var session = Store.OpenAsyncSession();
        var employeeEntity = await session.LoadAsync<BR_Employee>("employees/1");

        var result = await resolver.ResolveAsync(session, [employeeEntity], employee);

        result.Get("issuers/1").Should().Be("Microsoft");
        result.Get("issuers/2").Should().Be("Google");
    }

    [Fact]
    public async Task AsDetail_nested_reference_renders_the_targets_own_recursive_breadcrumb()
    {
        // The embedded credit's breadcrumb token {LabelId} points to a reference attribute, so it
        // must render the Label's OWN breadcrumb — which itself contains a reference ({CountryId}).
        // i.e. a breadcrumb token pointing to a reference renders that reference's breadcrumb,
        // recursively, even two AsDetail levels removed from the root.
        using (var seed = Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new BR_Country { Name = "Japan" }, "countries/1");
            await seed.StoreAsync(new BR_Label { Name = "Sony", CountryId = "countries/1" }, "labels/1");
            await seed.StoreAsync(new BR_Release { Title = "Thriller", Credits = [new BR_Credit { LabelId = "labels/1" }] }, "releases/1");
            await seed.SaveChangesAsync();
        }

        var country = Def(typeof(BR_Country), "{Name}", null, Scalar("Name"));
        var label = Def(typeof(BR_Label), "{Name} ({CountryId})", null, Scalar("Name"), Ref("CountryId", typeof(BR_Country)));
        var credit = Def(typeof(BR_Credit), "{LabelId}", null, Ref("LabelId", typeof(BR_Label)));
        var release = Def(typeof(BR_Release), "{Title}", null, Scalar("Title"), AsDetailArr("Credits", typeof(BR_Credit)));
        var (resolver, _) = Build(country, label, credit, release);

        using var session = Store.OpenAsyncSession();
        var releaseEntity = await session.LoadAsync<BR_Release>("releases/1");

        var result = await resolver.ResolveAsync(session, [releaseEntity], release);

        result.Get("labels/1").Should().Be("Sony (Japan)", "the AsDetail-nested reference renders the label's full breadcrumb, which itself expands {CountryId}");
        result.Get("countries/1").Should().Be("Japan");
    }
}
