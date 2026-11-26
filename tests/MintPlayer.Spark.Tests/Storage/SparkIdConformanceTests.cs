using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage.InMemory;
using MintPlayer.Spark.Storage.Raven;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// <see cref="Id{T}"/> on every provider (#536 D23, D7): new ids, parsing, the round trip of an entity's own id and its
/// references, and LINQ over them. The entities carry the <c>__DocumentId</c> member by hand, exactly as
/// <c>DocumentIdGenerator</c> emits it (its own tests pin the emitted text), because this project doesn't run the
/// library generators.
/// </summary>
public abstract class SparkIdConformanceTests : IAsyncLifetime
{
    public class IdCompany
    {
        public Id<IdCompany> Id { get; set; }
        public string Name { get; set; } = string.Empty;

        internal string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = Id<IdCompany>.FromTrusted(value); }
    }

    public class IdPerson
    {
        public Id<IdPerson> Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public Id<IdCompany>? CompanyId { get; set; }
        public List<Id<IdPerson>> FriendIds { get; set; } = [];

        internal string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = Id<IdPerson>.FromTrusted(value); }
    }

    /// <summary>A base type that owns the id, as MintPlayer's <c>Subject</c> does for songs and artists.</summary>
    public abstract class IdSubject
    {
        public Id<IdSubject> Id { get; set; }

        internal string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = Id<IdSubject>.FromTrusted(value); }
    }

    public class IdSong : IdSubject
    {
        public string Title { get; set; } = string.Empty;
    }

    /// <summary>Natural ids from the attribute alone, as CodeCoverage's entities declare them.</summary>
    [IdKind(SparkIdKind.Natural)]
    public class IdPostcode
    {
        public Id<IdPostcode> Id { get; set; }

        internal string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = Id<IdPostcode>.FromTrusted(value); }
    }

    protected ISparkStore Store { get; private set; } = null!;

    protected ISparkIds Ids { get; private set; } = null!;

    protected abstract Task<ISparkStore> CreateStoreAsync();

    protected virtual void AddProviderServices(IServiceCollection services) { }

    protected virtual IQueryable<T> Settled<T>(IQueryable<T> query) => query;

    public virtual async Task InitializeAsync()
    {
        Store = await CreateStoreAsync();
        var services = new ServiceCollection();
        services.AddSingleton(Store);
        services.AddOptions();
        services.AddSingleton<ISparkIds, SparkIds>();
        AddProviderServices(services);
        Ids = services.BuildServiceProvider().GetRequiredService<ISparkIds>();
    }

    public virtual Task DisposeAsync() => Task.CompletedTask;

    private async Task<(IdCompany Acme, IdPerson Ann, IdPerson Bob)> SeedAsync()
    {
        var acme = new IdCompany { Id = await Ids.NewAsync<IdCompany>(), Name = "Acme" };
        var ann = new IdPerson { Id = await Ids.NewAsync<IdPerson>(), Name = "Ann", CompanyId = acme.Id };
        var bob = new IdPerson { Id = await Ids.NewAsync<IdPerson>(), Name = "Bob", FriendIds = [ann.Id] };

        using var session = Store.OpenSession();
        await session.StoreAsync(acme);
        await session.StoreAsync(ann);
        await session.StoreAsync(bob);
        await session.SaveChangesAsync();
        return (acme, ann, bob);
    }

    [Fact]
    public async Task A_new_guid_id_is_canonical_and_lower_case()
    {
        var id = await Ids.NewAsync<IdPerson>();

        id.IsEmpty.Should().BeFalse();
        id.Value.Should().Be(id.Value.ToLowerInvariant());
        id.Collection.Should().Be(Store.GetCollectionName(typeof(IdPerson)).ToLowerInvariant());
        Guid.TryParseExact(id.Key, "D", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Parse_lowers_accepts_the_key_alone_and_refuses_another_collection()
    {
        var prefix = Store.GetCollectionName(typeof(IdPerson)) + "/";
        var guid = Guid.NewGuid();

        Ids.Parse<IdPerson>(prefix.ToUpperInvariant() + guid.ToString("N").ToUpperInvariant()).Value
            .Should().Be(prefix.ToLowerInvariant() + guid.ToString("D"));
        Ids.Parse<IdPerson>(guid.ToString("D")).Value.Should().Be(prefix.ToLowerInvariant() + guid.ToString("D"));
        Ids.TryParse<IdPerson>(Store.GetCollectionName(typeof(IdCompany)) + "/" + guid, out _).Should().BeFalse();
        Ids.TryParse<IdPerson>("  ", out var empty).Should().BeFalse();
        empty.IsEmpty.Should().BeTrue();
        var act = () => Ids.Parse<IdPerson>("");
        act.Should().Throw<FormatException>();
    }

    /// <summary>
    /// D23 rule 2: <see cref="IdKindAttribute"/> sets the kind. A natural key is kept as written (lowered), where a
    /// Guid-kind key is normalised, it may hold a <c>/</c>, and <c>NewAsync</c> can't make one.
    /// </summary>
    [Fact]
    public async Task The_IdKind_attribute_makes_a_natural_key_kept_as_written()
    {
        var prefix = Store.GetCollectionName(typeof(IdPostcode)).ToLowerInvariant() + "/";

        Ids.KindOf(typeof(IdPostcode)).Should().Be(SparkIdKind.Natural);
        Ids.Parse<IdPostcode>("042").Value.Should().Be(prefix + "042");
        Ids.Parse<IdPostcode>("BE/9000").Value.Should().Be(prefix + "be/9000");
        Ids.Parse<IdPerson>("042").Key.Should().Be("42");

        var act = () => Ids.NewAsync<IdPostcode>();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// The id declared on a base type: the derived entity is stored under its own collection and loads with its id.
    /// RavenDB looks for the identity member on the derived type, where a base's private member is invisible.
    /// </summary>
    [Fact]
    public async Task An_id_declared_on_a_base_type_round_trips()
    {
        var song = new IdSong { Id = Id<IdSubject>.FromTrusted("idsongs/sky"), Title = "Sky" };
        using (var session = Store.OpenSession())
        {
            await session.StoreAsync(song);
            await session.SaveChangesAsync();
        }

        using var read = Store.OpenSession();
        var loaded = await read.LoadAsync<IdSong>("IdSongs/sky");

        loaded.Should().NotBeNull();
        loaded!.Id.Value.Should().Be("idsongs/sky");
        loaded.Title.Should().Be("Sky");
    }

    [Fact]
    public async Task The_own_id_and_the_references_round_trip()
    {
        var (acme, ann, bob) = await SeedAsync();

        using var session = Store.OpenSession();
        var loadedAnn = await session.LoadAsync<IdPerson>(ann.Id.Value);
        var loadedBob = await session.LoadAsync<IdPerson>(bob.Id.Value.ToUpperInvariant());

        loadedAnn!.Id.Should().Be(ann.Id);
        loadedAnn.CompanyId.Should().Be(acme.Id);
        loadedBob!.Id.Should().Be(bob.Id);
        loadedBob.CompanyId.Should().BeNull();
        loadedBob.FriendIds.Should().Equal([ann.Id]);
        session.GetId(loadedAnn).Should().Be(ann.Id.Value);
    }

    [Fact]
    public async Task Linq_over_the_own_id_filters_and_orders()
    {
        var (_, ann, bob) = await SeedAsync();
        var wanted = new List<Id<IdPerson>> { ann.Id, bob.Id };

        using var session = Store.OpenSession();
        var byEquality = await session.ToListAsync(Settled(session.Query<IdPerson>()).Where(p => p.Id == ann.Id));
        var byInequality = await session.ToListAsync(Settled(session.Query<IdPerson>()).Where(p => p.Id != ann.Id));
        var byContains = await session.ToListAsync(Settled(session.Query<IdPerson>()).Where(p => wanted.Contains(p.Id)));
        var ordered = await session.ToListAsync(Settled(session.Query<IdPerson>()).OrderBy(p => p.Id));

        byEquality.Select(p => p.Name).Should().Equal(["Ann"]);
        byInequality.Select(p => p.Name).Should().Equal(["Bob"]);
        byContains.Select(p => p.Name).Should().BeEquivalentTo(["Ann", "Bob"]);
        ordered.Select(p => p.Id.Value).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public async Task Linq_over_a_reference_id_filters()
    {
        var (acme, _, _) = await SeedAsync();
        var companies = new Id<IdCompany>?[] { acme.Id };

        using var session = Store.OpenSession();
        var byEquality = await session.ToListAsync(Settled(session.Query<IdPerson>()).Where(p => p.CompanyId == acme.Id));
        var byContains = await session.ToListAsync(Settled(session.Query<IdPerson>()).Where(p => companies.Contains(p.CompanyId)));
        var count = await session.CountAsync(Settled(session.Query<IdPerson>()).Where(p => p.CompanyId == acme.Id));

        byEquality.Select(p => p.Name).Should().Equal(["Ann"]);
        byContains.Select(p => p.Name).Should().Equal(["Ann"]);
        count.Should().Be(1);
    }
}

/// <summary>The suite against RavenDB, an embedded server per class.</summary>
public sealed class RavenSparkIdConformanceTests : SparkIdConformanceTests
{
    private readonly Driver driver = new();

    protected override async Task<ISparkStore> CreateStoreAsync()
    {
        await driver.InitializeAsync();
        return new RavenSparkStore(driver.DocumentStore);
    }

    protected override void AddProviderServices(IServiceCollection services)
    {
        services.AddSingleton(driver.DocumentStore);
        services.AddSingleton<ISparkIntegerIdSource, RavenIntegerIdSource>();
    }

    protected override IQueryable<T> Settled<T>(IQueryable<T> query)
        => ((IRavenQueryable<T>)query).Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)));

    public override async Task DisposeAsync() => await driver.DisposeAsync();

    /// <summary>D23 rule 6: the id is the document's key, never a body field; a reference is stored as a plain string.</summary>
    [Fact]
    public async Task The_document_body_has_no_id_and_a_string_reference()
    {
        var acme = new IdCompany { Id = await Ids.NewAsync<IdCompany>(), Name = "Acme" };
        var ann = new IdPerson { Id = await Ids.NewAsync<IdPerson>(), Name = "Ann", CompanyId = acme.Id };
        using (var session = Store.OpenSession())
        {
            await session.StoreAsync(ann);
            await session.SaveChangesAsync();
        }

        using var raw = driver.DocumentStore.OpenAsyncSession();
        var document = await raw.LoadAsync<Dictionary<string, object?>>(ann.Id.Value);

        document.Should().NotContainKey("Id");
        document!["CompanyId"].Should().Be(acme.Id.Value);
    }

    /// <summary>D23 rule 6: an Integer-kind id is RavenDB's native HiLo, node tag kept, lower case.</summary>
    [Fact]
    public async Task An_integer_id_is_a_lower_case_hilo_key()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Store);
        services.AddSingleton(driver.DocumentStore);
        services.Configure<SparkIdOptions>(o => o.Kinds[typeof(IdCompany)] = SparkIdKind.Integer);
        services.AddSingleton<ISparkIntegerIdSource, RavenIntegerIdSource>();
        services.AddSingleton<ISparkIds, SparkIds>();
        var ids = services.BuildServiceProvider().GetRequiredService<ISparkIds>();

        var first = await ids.NewAsync<IdCompany>();
        var second = await ids.NewAsync<IdCompany>();

        first.Key.Should().MatchRegex("^[0-9]+-[a-z]+$");
        second.Should().NotBe(first);
    }

    private sealed class Driver : SparkTestDriver
    {
        public IDocumentStore DocumentStore => Store;
    }
}

/// <summary>The suite against the in-memory store.</summary>
public sealed class InMemorySparkIdConformanceTests : SparkIdConformanceTests
{
    protected override Task<ISparkStore> CreateStoreAsync()
        => Task.FromResult<ISparkStore>(new InMemorySparkStore(new InMemoryDatabase()));
}
