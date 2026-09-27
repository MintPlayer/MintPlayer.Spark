using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Claim renewal and release, which decide whether a message is being worked on and by whom.
/// Another owner is simulated by writing its id onto the message — the same data another host
/// would leave behind.
/// </summary>
public class MessageClaimsTests : SparkTestDriver
{
    private const string Us = "this-node/0001";
    private const string Them = "other-node/0002";

    private async Task<string> SeedMessageAsync(string? owner, EMessageStatus status = EMessageStatus.Processing, int attempts = 1)
    {
        var message = new SparkMessage
        {
            QueueName = "claims-tests",
            MessageType = "irrelevant",
            PayloadJson = "{}",
            Status = status,
            OwnerId = owner,
            AttemptCount = attempts,
            ClaimExpiresAtUtc = DateTime.UtcNow.AddSeconds(5),
        };
        await SeedAsync(session => session.StoreAsync(message));
        return message.Id!;
    }

    private async Task<SparkMessage> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<SparkMessage>(id);
    }

    [Fact]
    public async Task Renewing_our_own_claim_extends_it()
    {
        var id = await SeedMessageAsync(Us);
        var before = (await LoadAsync(id)).ClaimExpiresAtUtc;

        (await MessageClaims.TryRenewAsync(Store, id, Us, TimeSpan.FromMinutes(5), CancellationToken.None)).Should().BeTrue();

        (await LoadAsync(id)).ClaimExpiresAtUtc!.Value.Should().BeAfter(before!.Value);
    }

    [Theory]
    [InlineData(Them, EMessageStatus.Processing)]
    [InlineData(Us, EMessageStatus.Failed)]
    [InlineData(null, EMessageStatus.Pending)]
    public async Task A_claim_that_is_no_longer_ours_is_not_renewed(string? owner, EMessageStatus status)
    {
        var id = await SeedMessageAsync(owner, status);
        var before = (await LoadAsync(id)).ClaimExpiresAtUtc;

        (await MessageClaims.TryRenewAsync(Store, id, Us, TimeSpan.FromMinutes(5), CancellationToken.None)).Should().BeFalse();

        ((await LoadAsync(id)).ClaimExpiresAtUtc == before).Should().BeTrue("nothing was written");
    }

    [Fact]
    public async Task Renewing_a_deleted_message_reports_the_claim_lost()
        => (await MessageClaims.TryRenewAsync(Store, "SparkMessages/gone", Us, TimeSpan.FromMinutes(5), CancellationToken.None))
            .Should().BeFalse();

    [Fact]
    public async Task Releasing_an_unstarted_claim_requeues_it_without_spending_an_attempt()
    {
        var id = await SeedMessageAsync(Us, attempts: 2);

        await MessageClaims.ReleaseUnstartedAsync(Store, id, Us, CancellationToken.None);

        var message = await LoadAsync(id);
        message.Status.Should().Be(EMessageStatus.Pending);
        message.OwnerId.Should().BeNull();
        message.ClaimExpiresAtUtc.Should().NotHaveValue();
        message.WakeUp.Should().BeTrue("the feeder must see it again");
        message.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task Releasing_never_takes_an_attempt_below_zero()
    {
        var id = await SeedMessageAsync(Us, attempts: 0);

        await MessageClaims.ReleaseUnstartedAsync(Store, id, Us, CancellationToken.None);

        (await LoadAsync(id)).AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task Releasing_someone_elses_claim_or_a_missing_message_changes_nothing()
    {
        var id = await SeedMessageAsync(Them);

        await MessageClaims.ReleaseUnstartedAsync(Store, id, Us, CancellationToken.None);
        await MessageClaims.ReleaseUnstartedAsync(Store, "SparkMessages/gone", Us, CancellationToken.None);

        var message = await LoadAsync(id);
        message.OwnerId.Should().Be(Them);
        message.Status.Should().Be(EMessageStatus.Processing);
    }

    [Fact]
    public async Task Of_two_concurrent_claims_exactly_one_wins()
    {
        var id = await SeedMessageAsync(owner: null, status: EMessageStatus.Pending, attempts: 0);

        using var first = Store.OpenAsyncSession();
        using var second = Store.OpenAsyncSession();
        first.Advanced.UseOptimisticConcurrency = true;
        second.Advanced.UseOptimisticConcurrency = true;
        var a = await first.LoadAsync<SparkMessage>(id);
        var b = await second.LoadAsync<SparkMessage>(id);

        var won = await MessageClaims.TryClaimAsync(first, a, Us, TimeSpan.FromMinutes(1), CancellationToken.None);
        var lost = await MessageClaims.TryClaimAsync(second, b, Them, TimeSpan.FromMinutes(1), CancellationToken.None);

        won.Should().BeTrue();
        lost.Should().BeFalse("the second writer read a version the first has already replaced");
        (await LoadAsync(id)).OwnerId.Should().Be(Us);
    }
}
