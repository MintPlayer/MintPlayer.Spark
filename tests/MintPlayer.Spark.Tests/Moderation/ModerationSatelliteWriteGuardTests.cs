using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Moderation.Documents;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Moderation;

/// <summary>
/// Contributions M5 (PRD S-C5): Moderation's veto over satellite writes — documents written on the
/// caller's behalf that are not PO saves (a contribution). The contributions side of the seam (a guard
/// refusing writes nothing) is <c>ContributionsRuntimeTests</c>; this is Moderation's answer.
/// </summary>
public class ModerationSatelliteWriteGuardTests : SparkTestDriver
{
    private static async Task EnsureAsync(MoHost host, Type documentType, string documentId)
    {
        using var scope = host.Factory.CreateScope();
        var guards = scope.ServiceProvider.GetServices<ISatelliteWriteGuard>().ToList();
        guards.Should().ContainSingle("AddModeration registers its guard once");
        await guards[0].EnsureMayWriteAsync(new SatelliteWriteContext
        {
            DocumentType = documentType,
            DocumentId = documentId,
            TargetType = typeof(MoPlain),
            TargetId = "MoPlains/1",
        });
    }

    [Fact]
    public async Task A_locked_moderatable_document_is_refused_and_an_unlocked_one_or_a_plain_type_is_not()
    {
        await using var host = await MoHost.StartAsync(Store);
        await SeedAsync(session => session.StoreAsync(new ModerationLock(), ModerationIds.Lock("MoPosts/locked")));

        var ex = await Assert.ThrowsAsync<SparkValidationException>(() => EnsureAsync(host, typeof(MoPost), "MoPosts/locked"));
        ex.Message.Should().Contain("locked");

        await EnsureAsync(host, typeof(MoPost), "MoPosts/open");
        await EnsureAsync(host, typeof(MoPlain), "MoPosts/locked");
    }
}
