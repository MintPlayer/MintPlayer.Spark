using MintPlayer.Spark.IdentityProvider.Migrations;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Migrations;

/// <summary>
/// Parity PRD A12: replay markers moved from <c>OidcTokens</c> documents to compare-exchange, so
/// <see cref="M_202610131600_ReplayMarkersLeaveOidcTokens"/> deletes the old markers and nothing else.
/// </summary>
public class ReplayMarkersLeaveOidcTokensMigrationTests : SparkTestDriver
{
    [Fact]
    public async Task Replay_markers_are_deleted_and_other_tokens_are_left_alone()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new OidcToken { Type = "dpop_jti", Status = "redeemed" }, "OidcTokens/replay:marker");
            await session.StoreAsync(new OidcToken { Type = OidcTokenTypes.AccessToken }, "OidcTokens/access");
            await session.SaveChangesAsync();
        }
        var migration = new M_202610131600_ReplayMarkersLeaveOidcTokens(Store);

        await migration.UpAsync(CancellationToken.None);
        await migration.UpAsync(CancellationToken.None);

        using var check = Store.OpenAsyncSession();
        (await check.Advanced.ExistsAsync("OidcTokens/replay:marker")).Should().BeFalse();
        (await check.Advanced.ExistsAsync("OidcTokens/access")).Should().BeTrue("only the replay markers are deleted");
    }
}
