using MintPlayer.Spark.Claims;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Claims;

/// <summary>
/// Commerce X12: Spark's claims primitive over compare-exchange. Reserve, convert and release a single-owner
/// key with lazy expiry (an expired claim is taken over, guarded by the index read), idempotent by owner, and a
/// cluster-wide unit of work that commits several keys and documents together or not at all.
/// </summary>
public class SparkClaimsTests : SparkTestDriver
{
    private static readonly DateTime Start = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SparkClaimKind<SparkClaimHold> Seats = SparkClaimKinds.Holds("tests/seats/");

    private readonly ManualClock clock = new(Start);

    private SparkClaims Claims() => new(Store, clock);

    [Fact]
    public async Task A_free_key_is_reserved_once_and_reserving_it_again_is_idempotent_by_owner()
    {
        var claims = Claims();
        var key = Seats.Key("A1");

        var first = await claims.ReserveHoldAsync(Seats, key, "order/1", Start.AddMinutes(10));
        var again = await claims.ReserveHoldAsync(Seats, key, "order/1", Start.AddMinutes(20));
        var other = await claims.ReserveHoldAsync(Seats, key, "order/2", Start.AddMinutes(10));

        first.Status.Should().Be(SparkClaimStatus.Reserved);
        again.Status.Should().Be(SparkClaimStatus.AlreadyYours);
        again.Claim!.Value.ExpiresAtUtc.Should().Be(Start.AddMinutes(10), "an idempotent reservation writes nothing");
        other.Status.Should().Be(SparkClaimStatus.Taken);
        other.Claim!.Value.Owner.Should().Be("order/1");
    }

    [Fact]
    public async Task An_expired_hold_is_absent_and_the_next_reservation_takes_it_over()
    {
        var claims = Claims();
        var key = Seats.Key("A2");
        await claims.ReserveHoldAsync(Seats, key, "order/1", Start.AddMinutes(10));

        clock.Advance(TimeSpan.FromMinutes(11));
        (await claims.GetAsync(Seats, key)).Should().BeNull("lazy expiry: an expired claim reads as absent, though nothing deleted it");

        SparkClaim<SparkClaimHold>? previous = null;
        var takeover = await claims.ReserveAsync(Seats, key, "order/2", p => { previous = p; return new SparkClaimHold("order/2", Start.AddMinutes(30)); });

        takeover.Status.Should().Be(SparkClaimStatus.Reserved);
        previous!.Value.Owner.Should().Be("order/1", "the value function sees the claim it takes over");
        (await claims.GetAsync(Seats, key))!.Value.Owner.Should().Be("order/2");
    }

    [Fact]
    public async Task Grace_keeps_an_expired_hold_live_for_clock_skew()
    {
        var kind = SparkClaimKinds.Holds("tests/graced/", grace: TimeSpan.FromMinutes(1));
        var claims = Claims();
        var key = kind.Key("A3");
        await claims.ReserveHoldAsync(kind, key, "order/1", Start.AddMinutes(10));

        clock.Advance(TimeSpan.FromMinutes(10.5));
        (await claims.ReserveHoldAsync(kind, key, "order/2", Start.AddMinutes(30))).Status.Should().Be(SparkClaimStatus.Taken);

        clock.Advance(TimeSpan.FromMinutes(1));
        (await claims.ReserveHoldAsync(kind, key, "order/2", Start.AddMinutes(30))).Status.Should().Be(SparkClaimStatus.Reserved);
    }

    [Fact]
    public async Task Of_many_concurrent_reservations_of_one_key_exactly_one_wins()
    {
        var key = Seats.Key("A4");

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
        {
            await Task.Yield();
            return await Claims().ReserveHoldAsync(Seats, key, $"order/{i}", Start.AddMinutes(10));
        }));

        results.Count(r => r.Status == SparkClaimStatus.Reserved).Should().Be(1);
        results.Where(r => r.Status != SparkClaimStatus.Reserved).Should().OnlyContain(r => r.Status == SparkClaimStatus.Taken || r.Status == SparkClaimStatus.Conflict);
    }

    [Fact]
    public async Task A_converted_hold_never_expires_and_only_its_owner_converts_or_releases_it()
    {
        var claims = Claims();
        var key = Seats.Key("A5");
        await claims.ReserveHoldAsync(Seats, key, "order/1", Start.AddMinutes(10));

        (await claims.ConvertAsync(Seats, key, "order/2")).Status.Should().Be(SparkClaimStatus.NotYours);
        (await claims.ConvertAsync(Seats, key, "order/1")).Status.Should().Be(SparkClaimStatus.Updated);

        clock.Advance(TimeSpan.FromDays(365));
        (await claims.ReserveHoldAsync(Seats, key, "order/2", Start.AddDays(400))).Status.Should().Be(SparkClaimStatus.Taken, "a sold seat stays sold");

        (await claims.ReleaseAsync(Seats, key, "order/2")).Status.Should().Be(SparkClaimStatus.NotYours, "another owner's claim is never released");
        (await claims.ReleaseAsync(Seats, key, "order/1")).Status.Should().Be(SparkClaimStatus.Released);
        (await claims.GetAsync(Seats, key)).Should().BeNull();
    }

    [Fact]
    public async Task A_live_claim_the_kind_calls_abandoned_is_taken_over()
    {
        var kind = new SparkClaimKind<string>("tests/emails/", owner => owner)
        {
            IsAbandonedAsync = async (claim, cancellationToken) =>
            {
                using var session = Store.OpenAsyncSession();
                return !await session.Advanced.ExistsAsync(claim.Value, cancellationToken);
            },
        };
        var claims = Claims();
        var key = kind.Key("someone@example.org");
        (await claims.ReserveAsync(kind, key, "users/gone", _ => "users/gone")).Status.Should().Be(SparkClaimStatus.Reserved);

        var result = await claims.ReserveAsync(kind, key, "users/new", _ => "users/new");

        result.Status.Should().Be(SparkClaimStatus.Reserved, "the owner document never existed, so the reservation is stale");
        (await claims.GetAsync(kind, key))!.Value.Should().Be("users/new");
    }

    [Fact]
    public async Task A_declining_value_function_writes_nothing()
    {
        var claims = Claims();
        var key = Seats.Key("A6");

        var result = await claims.ReserveAsync<SparkClaimHold>(Seats, key, "order/1", _ => null);

        result.Status.Should().Be(SparkClaimStatus.Declined);
        (await claims.GetAsync(Seats, key)).Should().BeNull();
    }

    [Fact]
    public async Task A_key_under_another_prefix_is_refused()
    {
        var act = () => Claims().GetAsync(Seats, new SparkClaimKey("other/A1"));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ---- the unit of work ----------------------------------------------------------------------

    [Fact]
    public async Task A_unit_of_work_commits_its_keys_and_documents_together()
    {
        var claims = Claims();
        using (var unitOfWork = claims.BeginUnitOfWork())
        {
            (await unitOfWork.ReserveHoldAsync(Seats, Seats.Key("B1"), "order/1", Start.AddMinutes(10))).Status.Should().Be(SparkClaimStatus.Reserved);
            (await unitOfWork.ReserveHoldAsync(Seats, Seats.Key("B2"), "order/1", Start.AddMinutes(10))).Status.Should().Be(SparkClaimStatus.Reserved);
            await unitOfWork.Session.StoreAsync(new ClaimOrder { Seats = ["B1", "B2"] }, "ClaimOrders/1");
            (await unitOfWork.CommitAsync()).Should().BeTrue();
        }

        (await claims.GetAsync(Seats, Seats.Key("B1")))!.Value.Owner.Should().Be("order/1");
        (await claims.GetAsync(Seats, Seats.Key("B2")))!.Value.Owner.Should().Be("order/1");
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<ClaimOrder>("ClaimOrders/1")).Should().NotBeNull();
    }

    [Fact]
    public async Task A_unit_of_work_that_lost_one_key_writes_nothing()
    {
        var claims = Claims();
        using var unitOfWork = claims.BeginUnitOfWork();
        await unitOfWork.ReserveHoldAsync(Seats, Seats.Key("C1"), "order/1", Start.AddMinutes(10));
        await unitOfWork.ReserveHoldAsync(Seats, Seats.Key("C2"), "order/1", Start.AddMinutes(10));
        await unitOfWork.Session.StoreAsync(new ClaimOrder { Seats = ["C1", "C2"] }, "ClaimOrders/2");

        // Another buyer takes C2 between the read and the commit.
        (await claims.ReserveHoldAsync(Seats, Seats.Key("C2"), "order/2", Start.AddMinutes(10))).Status.Should().Be(SparkClaimStatus.Reserved);

        (await unitOfWork.CommitAsync()).Should().BeFalse();
        (await claims.GetAsync(Seats, Seats.Key("C1"))).Should().BeNull("all or nothing: the key that was free is not held either");
        (await claims.GetAsync(Seats, Seats.Key("C2")))!.Value.Owner.Should().Be("order/2");
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<ClaimOrder>("ClaimOrders/2")).Should().BeNull("nor is the document written");
    }

    [Fact]
    public async Task A_unit_of_work_releases_and_converts_only_its_owners_claims()
    {
        var claims = Claims();
        await claims.ReserveHoldAsync(Seats, Seats.Key("D1"), "order/1", Start.AddMinutes(10));
        await claims.ReserveHoldAsync(Seats, Seats.Key("D2"), "order/1", Start.AddMinutes(10));

        using (var unitOfWork = claims.BeginUnitOfWork())
        {
            (await unitOfWork.ReleaseAsync(Seats, Seats.Key("D1"), "order/2")).Status.Should().Be(SparkClaimStatus.NotYours);
            (await unitOfWork.ReleaseAsync(Seats, Seats.Key("D1"), "order/1")).Status.Should().Be(SparkClaimStatus.Released);
            (await unitOfWork.ConvertAsync(Seats, Seats.Key("D2"), "order/1")).Status.Should().Be(SparkClaimStatus.Updated);
            (await unitOfWork.CommitAsync()).Should().BeTrue();
        }

        (await claims.GetAsync(Seats, Seats.Key("D1"))).Should().BeNull();
        (await claims.GetAsync(Seats, Seats.Key("D2")))!.Value.ExpiresAtUtc.HasValue.Should().BeFalse();
    }

    public sealed class ClaimOrder
    {
        public string? Id { get; set; }
        public List<string> Seats { get; set; } = [];
    }

    private sealed class ManualClock(DateTime start) : TimeProvider
    {
        private DateTimeOffset now = new(start, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}
