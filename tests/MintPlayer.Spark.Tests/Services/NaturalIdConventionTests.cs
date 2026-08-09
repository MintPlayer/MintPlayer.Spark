using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Pins <see cref="IHasNaturalId"/> against a real RavenDB instance.
/// <para>
/// The load-bearing assumption is RavenDB's, not Spark's: that a registered id convention is
/// consulted <i>before</i> <c>AsyncDocumentIdGenerator</c>. Spark installs both — the GUID
/// generator as the fallback for ordinary entities — so if that ordering were the other way
/// round, every natural id would silently become a GUID and every point-load by derived id would
/// silently miss. Asserting it here means the contract is read from the database rather than
/// assumed from the documentation.
/// </para>
/// </summary>
public class NaturalIdConventionTests : SparkTestDriver
{
    private class Car : IHasNaturalId
    {
        public static string GetId(string licencePlate) => $"cars/{licencePlate.ToUpperInvariant()}";
        string IHasNaturalId.GetId() => GetId(LicencePlate);

        public string? Id { get; set; }
        public string LicencePlate { get; set; } = null!;
        public string? Colour { get; set; }
    }

    private class Trailer
    {
        public string? Id { get; set; }
        public string? Label { get; set; }
    }

    // No PreInitialize override: SparkTestDriver installs Spark's id conventions for every
    // fixture. This file used to do it by hand, which is how the gap was noticed.
    private IDocumentStore SparkStore() => Store;

    [Fact]
    public async Task An_entity_that_derives_its_id_is_stored_under_it()
    {
        var store = SparkStore();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Car { LicencePlate = "1-abc-234", Colour = "red" });
            await session.SaveChangesAsync();
        }

        using var verify = store.OpenAsyncSession();
        var car = await verify.LoadAsync<Car>(Car.GetId("1-ABC-234"));

        car.Should().NotBeNull("the derived id must beat the GUID generator, which is also installed");
        car!.Colour.Should().Be("red");
    }

    [Fact]
    public async Task An_ordinary_entity_still_gets_a_generated_id()
    {
        var store = SparkStore();

        using var session = store.OpenAsyncSession();
        var trailer = new Trailer { Label = "flatbed" };
        await session.StoreAsync(trailer);
        await session.SaveChangesAsync();

        trailer.Id.Should().StartWith("Trailers/",
            "installing the natural-id convention must not disturb the default for everything else");
        Guid.TryParse(trailer.Id!["Trailers/".Length..], out _).Should().BeTrue();
    }

    /// <summary>
    /// The point of the whole exercise: a lookup by business key that does not go through an
    /// index, and therefore has no eventual-consistency window to read stale state in.
    /// </summary>
    [Fact]
    public async Task The_derived_id_makes_lookup_by_business_key_a_point_load()
    {
        var store = SparkStore();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Car { LicencePlate = "9-XYZ-000" });
            await session.SaveChangesAsync();
        }

        // Deliberately no WaitForIndexing: a point-load sees the write immediately, which an
        // index query would not be guaranteed to.
        using var verify = store.OpenAsyncSession();
        (await verify.LoadAsync<Car>(Car.GetId("9-xyz-000"))).Should().NotBeNull();
    }

    /// <summary>
    /// Storing the same natural id twice addresses one document, rather than creating a second.
    /// This is what makes a natural id an identity rather than a naming scheme.
    /// </summary>
    [Fact]
    public async Task Two_entities_with_the_same_natural_id_are_the_same_document()
    {
        var store = SparkStore();

        using (var first = store.OpenAsyncSession())
        {
            await first.StoreAsync(new Car { LicencePlate = "5-DUP-111", Colour = "blue" });
            await first.SaveChangesAsync();
        }

        using (var second = store.OpenAsyncSession())
        {
            await second.StoreAsync(new Car { LicencePlate = "5-DUP-111", Colour = "green" });
            await second.SaveChangesAsync();
        }

        using var verify = store.OpenAsyncSession();
        var car = await verify.LoadAsync<Car>(Car.GetId("5-DUP-111"));
        car!.Colour.Should().Be("green", "the second store overwrote the first, it did not duplicate it");
    }
}
