using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Commerce X3 / F6: a natural-id create is atomic. The collision probe in <c>DatabaseAccess</c> (security
/// sweep H2) answers before any write, so two creates of the same natural id that both pass it used to both
/// write, and the last one silently overwrote the first. The write is now create-only (RavenDB's empty
/// change vector, "must not exist"): exactly one racer wins, the other gets the 409 "already exists".
/// <para>
/// An expired <see cref="IExpiring"/> row under the id is a takeover, not a collision: every read path treats
/// it as missing (#453) while RavenDB may not sweep it for 36 hours, and a hold that expired must not block
/// the next buyer's hold on the same key for that long. The replacement expects the expired row's change
/// vector, so it is atomic too.
/// </para>
/// </summary>
/// <remarks>
/// The race is made deterministic by a before-save interceptor that holds every create until both have
/// arrived: by then both have passed the probe, and both writes are still to come. Fixture names start with
/// <c>Nat</c>: <c>ActionsResolver</c> matches actions classes by simple name.
/// </remarks>
public class NaturalIdAtomicCreateTests : SparkTestDriver
{
    private static readonly Guid CodeTypeId = Guid.Parse("a7031c00-0000-4000-8000-000000000001");
    private static readonly Guid HoldTypeId = Guid.Parse("a7031c00-0000-4000-8000-000000000002");

    private SparkEndpointFactory<NatContext> factory = null!;
    private NatRendezvous rendezvous = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        rendezvous = new NatRendezvous();
        factory = new SparkEndpointFactory<NatContext>(
            Store,
            [CodeModel(), HoldModel()],
            configureServices: services => services.AddSingleton(rendezvous),
            configureSpark: spark => spark.AddInterceptor<NatRendezvousInterceptor>(),
            security: SparkTestSecurity.Permissive);
    }

    public override async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Two_racing_creates_of_one_natural_id_store_exactly_one_and_refuse_the_other()
    {
        rendezvous.Expect(2);

        var results = await Task.WhenAll(
            CreateCodeAsync("ABC", "first"),
            CreateCodeAsync("ABC", "second"));

        rendezvous.Arrived.Should().Be(2, "both creates must have passed the probe before either wrote");
        results.Count(r => r is null).Should().Be(1, "exactly one create commits");
        var refusal = results.Single(r => r is not null);
        refusal.Should().BeOfType<SparkConcurrencyException>("the loser of the race is a 409, never a silent overwrite");
        ((SparkConcurrencyException)refusal!).Reason.Should().Be(SparkConcurrencyException.Exists);

        using var session = Store.OpenAsyncSession();
        var stored = await session.LoadAsync<NatCode>(NatCode.GetId("ABC"));
        stored.Should().NotBeNull();
        var winner = results[0] is null ? "first" : "second";
        stored!.Payload.Should().Be(winner, "the stored document is the winner's, not the last writer's");
    }

    [Fact]
    public async Task A_create_over_an_expired_hold_takes_the_id_over()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new NatHold { Code = "SEAT-1", Holder = "previous", ExpiresAtUtc = DateTime.UtcNow.AddHours(-1) }, NatHold.GetId("SEAT-1"));
            await session.SaveChangesAsync();
        }

        var refusal = await CreateHoldAsync("SEAT-1", "next");

        refusal.Should().BeNull("an expired hold is missing on every read path, so it cannot block the next create for up to 36 hours");
        using var check = Store.OpenAsyncSession();
        var stored = await check.LoadAsync<NatHold>(NatHold.GetId("SEAT-1"));
        stored!.Holder.Should().Be("next");
        stored.ExpiresAtUtc.HasValue.Should().BeFalse("the takeover is a create: it starts with no expiry of its own");
    }

    [Fact]
    public async Task A_create_over_a_live_hold_is_still_a_collision()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new NatHold { Code = "SEAT-2", Holder = "current", ExpiresAtUtc = DateTime.UtcNow.AddHours(1) }, NatHold.GetId("SEAT-2"));
            await session.SaveChangesAsync();
        }

        var refusal = await CreateHoldAsync("SEAT-2", "intruder");

        refusal.Should().BeOfType<SparkConcurrencyException>();
        ((SparkConcurrencyException)refusal!).Reason.Should().Be(SparkConcurrencyException.Exists);
        using var check = Store.OpenAsyncSession();
        (await check.LoadAsync<NatHold>(NatHold.GetId("SEAT-2")))!.Holder.Should().Be("current");
    }

    [Fact]
    public async Task Two_racing_takeovers_of_one_expired_hold_store_exactly_one()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new NatHold { Code = "SEAT-3", Holder = "previous", ExpiresAtUtc = DateTime.UtcNow.AddHours(-1) }, NatHold.GetId("SEAT-3"));
            await session.SaveChangesAsync();
        }
        rendezvous.Expect(2);

        var results = await Task.WhenAll(CreateHoldAsync("SEAT-3", "a"), CreateHoldAsync("SEAT-3", "b"));

        rendezvous.Arrived.Should().Be(2);
        results.Count(r => r is null).Should().Be(1, "the takeover expects the expired row's version, so only one replaces it");
        results.Single(r => r is not null).Should().BeOfType<SparkConcurrencyException>();
        using var check = Store.OpenAsyncSession();
        (await check.LoadAsync<NatHold>(NatHold.GetId("SEAT-3")))!.Holder.Should().Be(results[0] is null ? "a" : "b");
    }

    private Task<Exception?> CreateCodeAsync(string code, string payload)
        => CreateAsync(CodeTypeId, "NatCode", ("Code", code), ("Payload", payload));

    private Task<Exception?> CreateHoldAsync(string code, string holder)
        => CreateAsync(HoldTypeId, "NatHold", ("Code", code), ("Holder", holder));

    private async Task<Exception?> CreateAsync(Guid typeId, string name, params (string Name, string Value)[] values)
    {
        // Off the test's thread, so the two creates genuinely overlap.
        await Task.Yield();
        await using var scope = factory.GetService<IServiceScopeFactory>().CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
        var po = new PersistentObject { Id = null, ObjectTypeId = typeId, Name = name };
        foreach (var (attribute, value) in values)
            po.AddAttribute(new PersistentObjectAttribute { Name = attribute, DataType = "string", Value = value, IsValueChanged = true });
        return await Record.ExceptionAsync(() => db.SavePersistentObjectAsync(po));
    }

    private static EntityTypeFile CodeModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = CodeTypeId,
            Name = "NatCode",
            ClrType = typeof(NatCode).FullName!,
            Breadcrumb = "{Code}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Code", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Payload", DataType = "string" },
            ],
        },
    };

    private static EntityTypeFile HoldModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = HoldTypeId,
            Name = "NatHold",
            ClrType = typeof(NatHold).FullName!,
            Breadcrumb = "{Code}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Code", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Holder", DataType = "string" },
            ],
        },
    };
}

public class NatCode : IHasNaturalId
{
    public string? Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;

    public static string GetId(string code) => $"NatCodes/{code.ToUpperInvariant()}";
    string IHasNaturalId.GetId() => GetId(Code);
}

public class NatHold : IHasNaturalId, IExpiring
{
    public string? Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Holder { get; set; } = string.Empty;
    public DateTime? ExpiresAtUtc { get; set; }

    public static string GetId(string code) => $"NatHolds/{code.ToUpperInvariant()}";
    string IHasNaturalId.GetId() => GetId(Code);
}

public class NatContext : SparkContext
{
    public IRavenQueryable<NatCode> Codes => Session.Query<NatCode>();
    public IRavenQueryable<NatHold> Holds => Session.Query<NatHold>();
}

/// <summary>Holds each create until the expected number have arrived, after the probe and before the write.</summary>
public sealed class NatRendezvous
{
    private TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int expected;
    private int arrived;

    public int Arrived => Volatile.Read(ref arrived);

    public void Expect(int count)
    {
        expected = count;
        arrived = 0;
        all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task ArriveAsync()
    {
        if (expected == 0)
            return;
        if (Interlocked.Increment(ref arrived) >= expected)
            all.TrySetResult();
        // A failure bound, not a pace: both creates arrive within milliseconds.
        await all.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}

public sealed class NatRendezvousInterceptor(NatRendezvous rendezvous) : IBeforeSave
{
    public bool AppliesTo(Type entityType) => entityType == typeof(NatCode) || entityType == typeof(NatHold);

    public async ValueTask OnBeforeSaveAsync(SaveContext context) => await rendezvous.ArriveAsync();
}
