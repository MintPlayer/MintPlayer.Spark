using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Authorization.Migrations;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Migrations;

/// <summary>
/// #536: the user store finds users by name and by login through reservations. Users stored before held only their
/// email reservation; <see cref="M_202611251200_UserLookupReservations"/> claims the rest.
/// </summary>
public class UserLookupReservationsMigrationTests : RavenSparkTestDriver
{
    [Fact]
    public async Task Existing_users_are_found_by_name_and_login_after_the_migration()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new SparkUser
            {
                UserName = "jane",
                NormalizedUserName = "JANE",
                Logins = [new SparkUserLogin { LoginProvider = "GitHub", ProviderKey = "gh-1" }],
            }, "SparkUsers/1");
            // A duplicate name from before uniqueness was enforced: the first user keeps it, the second keeps working.
            await session.StoreAsync(new SparkUser
            {
                UserName = "Jane",
                NormalizedUserName = "JANE",
                Logins = [new SparkUserLogin { LoginProvider = "Google", ProviderKey = "g-2" }],
            }, "SparkUsers/2");
            await session.SaveChangesAsync();
        }

        var sparkStore = Store.AsSparkStore();
        using (var users = new UserStore<SparkUser>(sparkStore))
            (await users.FindByNameAsync("JANE", default)).Should().BeNull("nothing is reserved yet");

        var migration = new M_202611251200_UserLookupReservations(raven: Store, store: sparkStore);
        await migration.UpAsync(CancellationToken.None);
        await migration.UpAsync(CancellationToken.None); // a second run claims nothing new

        using (var users = new UserStore<SparkUser>(sparkStore))
        {
            (await users.FindByNameAsync("JANE", default))!.Id.Should().Be("SparkUsers/1");
            (await users.FindByLoginAsync("GitHub", "gh-1", default))!.Id.Should().Be("SparkUsers/1");
            (await users.FindByLoginAsync("Google", "g-2", default))!.Id.Should().Be("SparkUsers/2", "its other keys are still claimed");
        }
    }
}
