using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Authorization.Migrations;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Migrations;

/// <summary>
/// #264 G-Q22: registration used to set <c>UserName = email</c>, and the user name is shown to other
/// users. <see cref="M_202610051200_UserNamesAreNotEmails"/> gives every such account a generated handle.
/// </summary>
public class UserNamesAreNotEmailsMigrationTests : SparkTestDriver
{
    [Fact]
    public async Task Email_shaped_user_names_get_a_unique_generated_handle_and_others_are_left_alone()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(Legacy("jane@example.com"), "SparkUsers/1");
            await session.StoreAsync(Legacy("john@example.com"), "SparkUsers/2");
            await session.StoreAsync(new SparkUser { UserName = "handle", NormalizedUserName = "HANDLE", Email = "handle@example.com" }, "SparkUsers/3");
            await session.SaveChangesAsync();
        }

        await new M_202610051200_UserNamesAreNotEmails(Store).UpAsync(CancellationToken.None);

        using var check = Store.OpenAsyncSession();
        var jane = (await check.LoadAsync<SparkUser>("SparkUsers/1"))!;
        var john = (await check.LoadAsync<SparkUser>("SparkUsers/2"))!;
        var handle = (await check.LoadAsync<SparkUser>("SparkUsers/3"))!;

        foreach (var renamed in new[] { jane, john })
        {
            renamed.UserName.Should().MatchRegex("^user-[0-9a-f]{6}$");
            renamed.NormalizedUserName.Should().Be(renamed.UserName!.ToUpperInvariant());
        }
        jane.UserName.Should().NotBe(john.UserName);
        jane.Email.Should().Be("jane@example.com", "only the user name moves; sign-in by email keeps working");
        handle.UserName.Should().Be("handle");
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(Legacy("jane@example.com"), "SparkUsers/1");
            await session.SaveChangesAsync();
        }
        var migration = new M_202610051200_UserNamesAreNotEmails(Store);

        await migration.UpAsync(CancellationToken.None);
        var first = await ChangeVectorAsync("SparkUsers/1");
        await migration.UpAsync(CancellationToken.None);

        (await ChangeVectorAsync("SparkUsers/1")).Should().Be(first);
    }

    private static SparkUser Legacy(string email) => new()
    {
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
    };

    private async Task<string?> ChangeVectorAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        var document = await session.LoadAsync<SparkUser>(id);
        return session.Advanced.GetChangeVectorFor(document);
    }
}
