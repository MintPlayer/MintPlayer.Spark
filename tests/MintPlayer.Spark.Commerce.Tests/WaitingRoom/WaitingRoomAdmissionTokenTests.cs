using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.WaitingRoom;
using MintPlayer.Spark.WaitingRoom.Admissions;
using Raven.Client.Documents.Operations.CompareExchange;


namespace MintPlayer.Spark.Commerce.Tests.WaitingRoom;

using WaitingRoomMode = MintPlayer.Spark.WaitingRoom.WaitingRoomMode;

/// <summary>
/// The admission chain (§4.12, research §6.10 item 1; S12 item S2 reduced per round 4: correctness at CI size, no
/// 2,000-redemptions/s run): a token is sealed, bound to identity, room and phase, expires, and is spent exactly once.
/// </summary>
public class WaitingRoomAdmissionTokenTests : SparkTestDriver
{
    private const string Phase = "SalePhases/1";
    private readonly TestClock clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    private (string Token, string Jti) Issue(WaitingRoomHarness harness, string subject, string phase = Phase, int minutes = 30, bool poisoned = false)
        => harness.Services.GetRequiredService<WaitingRoomTokens>()
            .Issue("WaitingRooms/fest", phase, "WaitingRoomEntries/" + Guid.NewGuid().ToString("N"), subject, clock.GetUtcNow().AddMinutes(minutes), poisoned);

    [Fact]
    public async Task Zero_double_spends_under_concurrent_redeemers()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        const int tokens = 200;
        const int redeemers = 10;
        var issued = Enumerable.Range(0, tokens).Select(i => Issue(harness, $"user:u{i}")).ToList();

        var wins = new int[tokens];
        var refusals = new int[tokens];
        await Task.WhenAll(Enumerable.Range(0, tokens).SelectMany(t => Enumerable.Range(0, redeemers).Select(r => Task.Run(async () =>
        {
            await using var scope = harness.As(new TestVisitor(null, $"u{t}"));
            var admissions = scope.ServiceProvider.GetRequiredService<IWaitingRoomAdmissions>();
            var check = await admissions.ConsumeAsync(issued[t].Token, Phase, consumer: $"baskets/{t}-{r}");
            if (check.IsValid)
                Interlocked.Increment(ref wins[t]);
            else if (check.Status == WaitingRoomAdmissionStatus.Spent)
                Interlocked.Increment(ref refusals[t]);
        }))));

        wins.Should().OnlyContain(w => w == 1);
        refusals.Should().OnlyContain(r => r == redeemers - 1);
    }

    [Fact]
    public async Task The_same_consumer_retrying_is_idempotent_and_validation_sees_the_spend()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        var (token, _) = Issue(harness, "user:u1");
        await using var scope = harness.As(new TestVisitor(null, "u1"));
        var admissions = scope.ServiceProvider.GetRequiredService<IWaitingRoomAdmissions>();

        (await admissions.ValidateAsync(token, Phase)).IsValid.Should().BeTrue();
        (await admissions.ConsumeAsync(token, Phase, "baskets/1")).IsValid.Should().BeTrue();
        (await admissions.ConsumeAsync(token, Phase, "baskets/1")).IsValid.Should().BeTrue();
        (await admissions.ValidateAsync(token, Phase)).Status.Should().Be(WaitingRoomAdmissionStatus.Spent);
        (await admissions.ConsumeAsync(token, Phase, "baskets/2")).Status.Should().Be(WaitingRoomAdmissionStatus.Spent);
    }

    [Fact]
    public async Task Expired_wrong_phase_wrong_identity_forged_and_missing_are_refused()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        var (token, _) = Issue(harness, "user:u1", minutes: 30);
        await using var scope = harness.As(new TestVisitor(null, "u1"));
        var admissions = scope.ServiceProvider.GetRequiredService<IWaitingRoomAdmissions>();

        (await admissions.ValidateAsync(token, "SalePhases/2")).Status.Should().Be(WaitingRoomAdmissionStatus.WrongPhase);
        (await admissions.ValidateAsync(token, Phase, identityKey: "user:u2")).Status.Should().Be(WaitingRoomAdmissionStatus.WrongIdentity);
        (await admissions.ValidateAsync(token[..^4] + "AAAA", Phase)).Status.Should().Be(WaitingRoomAdmissionStatus.Forged);
        (await admissions.ValidateAsync(null, Phase)).Status.Should().Be(WaitingRoomAdmissionStatus.Missing);

        clock.Advance(TimeSpan.FromMinutes(30));
        (await admissions.ConsumeAsync(token, Phase, "baskets/1")).Status.Should().Be(WaitingRoomAdmissionStatus.Expired);
    }

    [Fact]
    public async Task A_visitors_token_stays_valid_for_the_account_it_signed_in_to()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        var (token, _) = Issue(harness, "visitor:v1");
        await using var scope = harness.As(new TestVisitor(null, "u1"));
        var admissions = scope.ServiceProvider.GetRequiredService<IWaitingRoomAdmissions>();

        (await admissions.ValidateAsync(token, Phase)).Status.Should().Be(WaitingRoomAdmissionStatus.WrongIdentity);
        // The X1 merge record (visitor-merge/{visitorId} → userId) is what links the two.
        await Store.Operations.SendAsync(new PutCompareExchangeValueOperation<string>("visitor-merge/v1", "u1", 0));
        (await admissions.ValidateAsync(token, Phase)).IsValid.Should().BeTrue();
        (await admissions.ValidateAsync(token, Phase, identityKey: "user:u2")).Status.Should().Be(WaitingRoomAdmissionStatus.WrongIdentity);
    }

    [Fact]
    public async Task A_poisoned_token_validates_and_is_refused_when_spent()
    {
        using var harness = new WaitingRoomHarness(Store, clock);
        var (token, _) = Issue(harness, "user:u1", poisoned: true);
        await using var scope = harness.As(new TestVisitor(null, "u1"));
        var admissions = scope.ServiceProvider.GetRequiredService<IWaitingRoomAdmissions>();

        (await admissions.ValidateAsync(token, Phase)).IsValid.Should().BeTrue();
        (await admissions.ConsumeAsync(token, Phase, "baskets/1")).Status.Should().Be(WaitingRoomAdmissionStatus.Poisoned);
    }

    [Fact]
    public async Task A_phase_is_gated_only_while_a_room_for_it_has_not_ended()
    {
        using var harness = new WaitingRoomHarness(Store, clock, o => o.GateCacheDuration = TimeSpan.Zero);
        await using var scope = harness.As(new TestVisitor("v1", null));
        var rooms = scope.ServiceProvider.GetRequiredService<ISparkWaitingRooms>();
        var admissions = scope.ServiceProvider.GetRequiredService<IWaitingRoomAdmissions>();

        (await admissions.IsGatedAsync(Phase)).Should().BeFalse();
        await rooms.SaveRoomAsync("fest", new WaitingRoomDefinition
        {
            Gate = Phase, Mode = WaitingRoomMode.Fifo, StartsAt = clock.GetUtcNow(), EndsAt = clock.GetUtcNow().AddHours(1),
        });
        await WaitForIndexesAsync();
        (await admissions.IsGatedAsync(Phase)).Should().BeTrue();
        clock.Advance(TimeSpan.FromHours(1));
        (await admissions.IsGatedAsync(Phase)).Should().BeFalse();
    }
}
