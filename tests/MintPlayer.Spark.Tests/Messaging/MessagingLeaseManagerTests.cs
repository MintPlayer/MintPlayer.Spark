using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Operations.CompareExchange;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// The leader lease, against a second node that exists only as data.
/// <para>
/// <see cref="MessageClaims.NodeId"/> is static, so two managers in one process are the same node
/// and can never contend. A foreign holder is forged instead, by writing the compare-exchange value
/// another host would have written. That is all a second host is, from the lease's point of view.
/// </para>
/// </summary>
public class MessagingLeaseManagerTests : SparkTestDriver
{
    private const string LeaseKey = "spark/messaging/leader";
    private const string ForeignNode = "other-host/ffff";

    private MessagingLeaseManager NewManager() => new(Store, NullLogger<MessagingLeaseManager>.Instance);

    private async Task ForgeAsync(string nodeId, DateTime expiresAtUtc, long fence = 7)
    {
        var result = await Store.Operations.SendAsync(new PutCompareExchangeValueOperation<MessagingLease>(
            LeaseKey, new MessagingLease(nodeId, expiresAtUtc.AddSeconds(-30), expiresAtUtc, fence), 0));
        result.Successful.Should().BeTrue();
    }

    private async Task<MessagingLease?> ReadAsync()
        => (await Store.Operations.SendAsync(new GetCompareExchangeValueOperation<MessagingLease>(LeaseKey)))?.Value;

    [Fact]
    public async Task A_free_lease_is_acquired_and_then_renewed_by_its_holder()
    {
        var manager = NewManager();

        (await manager.TryAcquireAsync(CancellationToken.None)).Should().BeTrue();
        var first = await ReadAsync();
        (await manager.TryAcquireAsync(CancellationToken.None)).Should().BeTrue("re-acquiring your own lease renews it");
        var second = await ReadAsync();

        first!.NodeId.Should().Be(MessageClaims.NodeId);
        second!.Fence.Should().Be(first.Fence, "a renewal is not a new tenure");
        (second.ExpiresAtUtc >= first.ExpiresAtUtc).Should().BeTrue();
    }

    [Fact]
    public async Task A_live_lease_held_by_another_node_is_not_taken()
    {
        await ForgeAsync(ForeignNode, DateTime.UtcNow.AddMinutes(1));

        (await NewManager().TryAcquireAsync(CancellationToken.None)).Should().BeFalse();
        (await ReadAsync())!.NodeId.Should().Be(ForeignNode);
    }

    [Fact]
    public async Task An_expired_lease_is_taken_over_with_the_next_fence()
    {
        await ForgeAsync(ForeignNode, DateTime.UtcNow.AddMinutes(-1), fence: 7);

        (await NewManager().TryAcquireAsync(CancellationToken.None)).Should().BeTrue();

        var lease = await ReadAsync();
        lease!.NodeId.Should().Be(MessageClaims.NodeId);
        lease.Fence.Should().Be(8);
        lease.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task Renewing_a_lease_that_does_not_exist_fails()
        => (await NewManager().TryRenewAsync(CancellationToken.None)).Should().BeFalse();

    [Fact]
    public async Task A_holder_that_was_evicted_cannot_renew_the_new_holders_lease()
    {
        await ForgeAsync(ForeignNode, DateTime.UtcNow.AddMinutes(1));
        var before = await ReadAsync();

        (await NewManager().TryRenewAsync(CancellationToken.None)).Should().BeFalse();

        (await ReadAsync())!.ExpiresAtUtc.Should().Be(before!.ExpiresAtUtc, "the other host's lease is untouched");
    }

    [Fact]
    public async Task Release_deletes_only_a_lease_this_node_holds()
    {
        await ForgeAsync(ForeignNode, DateTime.UtcNow.AddMinutes(1));
        var manager = NewManager();

        await manager.ReleaseAsync();
        (await ReadAsync()).Should().NotBeNull("releasing must never drop a lease another host took over");

        await Store.Operations.SendAsync(new DeleteCompareExchangeValueOperation<MessagingLease>(
            LeaseKey, (await Store.Operations.SendAsync(new GetCompareExchangeValueOperation<MessagingLease>(LeaseKey))).Index));
        (await manager.TryAcquireAsync(CancellationToken.None)).Should().BeTrue();

        await manager.ReleaseAsync();
        (await ReadAsync()).Should().BeNull();

        // Releasing with nothing held is a no-op.
        await manager.ReleaseAsync();
    }
}
