using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Authorization.Identity;

/// <summary>
/// #536 M6j: the user and role stores on the database <c>SPARK_TEST_DATABASE</c> picks. Emails and passkeys are claimed
/// through the store's reservations (compare exchange on RavenDB, under the same keys), names and logins are queried,
/// and the rare role and claim lookups stream the collection.
/// </summary>
public class NeutralUserStoreTests : SparkTestDriver
{
    public sealed class AuthContext : SparkContext;

    private SparkEndpointFactory<AuthContext> NewHost()
        => new(Database, [], configureSpark: spark => spark.AddAuthentication<SparkUser>(o => o.LocalCredentials = SparkLocalCredentials.SignInOnly));

    private static SparkUser NewUser(string name, string email) => new()
    {
        UserName = name,
        NormalizedUserName = name.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
    };

    [Fact]
    public async Task An_email_is_claimed_once_and_moves_with_the_user()
    {
        await using var host = NewHost();
        var store = host.GetService<ISparkStore>();

        string aliceId;
        using (var users = new UserStore<SparkUser>(store))
        {
            var alice = NewUser("alice", "alice@app.example");
            (await users.CreateAsync(alice, default)).Succeeded.Should().BeTrue();
            aliceId = alice.Id!;
            aliceId.Should().NotBeNullOrEmpty();

            var bob = NewUser("bob", "ALICE@app.example");
            var duplicate = await users.CreateAsync(bob, default);
            duplicate.Errors.Select(e => e.Code).Should().Equal("DuplicateEmail");

            var namesake = NewUser("ALICE", "other@app.example");
            (await users.CreateAsync(namesake, default)).Errors.Select(e => e.Code).Should().Equal("DuplicateUserName");

            // The failed saves left nothing behind: the user created first in this store still saves.
            alice.PhoneNumber = "+3200000000";
            (await users.UpdateAsync(alice, default)).Succeeded.Should().BeTrue();
        }

        using (var users = new UserStore<SparkUser>(store))
        {
            (await users.FindByEmailAsync("ALICE@APP.EXAMPLE", default))!.Id.Should().Be(aliceId);
            (await users.FindByNameAsync("ALICE", default))!.Id.Should().Be(aliceId);
            (await users.FindByNameAsync("BOB", default)).Should().BeNull("the duplicate wrote nothing");

            var alice = (await users.FindByIdAsync(aliceId, default))!;
            alice.PhoneNumber.Should().Be("+3200000000");
            await users.SetEmailAsync(alice, "alice@new.example", default);
            await users.SetNormalizedEmailAsync(alice, "ALICE@NEW.EXAMPLE", default);
            await users.SetUserNameAsync(alice, "alicia", default);
            await users.SetNormalizedUserNameAsync(alice, "ALICIA", default);
            (await users.UpdateAsync(alice, default)).Succeeded.Should().BeTrue();
        }

        using (var users = new UserStore<SparkUser>(store))
        {
            (await users.FindByNameAsync("ALICE", default)).Should().BeNull("the old name is released");
            (await users.FindByNameAsync("ALICIA", default))!.Id.Should().Be(aliceId);
            (await users.FindByEmailAsync("ALICE@APP.EXAMPLE", default)).Should().BeNull("the old address is released");
            (await users.FindByEmailAsync("ALICE@NEW.EXAMPLE", default))!.Id.Should().Be(aliceId);

            var carol = NewUser("carol", "alice@app.example");
            (await users.CreateAsync(carol, default)).Succeeded.Should().BeTrue("the released address is free again");
        }
    }

    [Fact]
    public async Task Logins_roles_and_claims_find_their_users()
    {
        await using var host = NewHost();
        var store = host.GetService<ISparkStore>();

        string id;
        using (var users = new UserStore<SparkUser>(store))
        {
            var dave = NewUser("dave", "dave@app.example");
            (await users.CreateAsync(dave, default)).Succeeded.Should().BeTrue();
            id = dave.Id!;
            await users.AddLoginAsync(dave, new UserLoginInfo("GitHub", "gh-42", "GitHub"), default);
            await users.AddToRoleAsync(dave, "Editors", default);
            await users.AddClaimsAsync(dave, [new Claim("department", "news")], default);
            (await users.UpdateAsync(dave, default)).Succeeded.Should().BeTrue();

            var erin = NewUser("erin", "erin@app.example");
            (await users.CreateAsync(erin, default)).Succeeded.Should().BeTrue();
        }

        using (var users = new UserStore<SparkUser>(store))
        {
            (await users.FindByLoginAsync("GitHub", "gh-42", default))!.Id.Should().Be(id);
            (await users.FindByLoginAsync("Google", "gh-42", default)).Should().BeNull("the provider must match too");
            (await users.GetUsersInRoleAsync("EDITORS", default)).Select(u => u.Id).Should().Equal(id);
            (await users.GetUsersForClaimAsync(new Claim("department", "news"), default)).Select(u => u.Id).Should().Equal(id);
        }
    }

    [Fact]
    public async Task A_passkey_belongs_to_one_user_and_a_delete_frees_its_reservations()
    {
        await using var host = NewHost();
        var store = host.GetService<ISparkStore>();
        var credential = Guid.NewGuid().ToByteArray();
        UserPasskeyInfo Passkey() => new(credential, [1, 2, 3], DateTimeOffset.UtcNow, 0, null, true, false, false, [], []);

        string frankId;
        using (var users = new UserStore<SparkUser>(store))
        {
            var frank = NewUser("frank", "frank@app.example");
            (await users.CreateAsync(frank, default)).Succeeded.Should().BeTrue();
            frankId = frank.Id!;
            await users.AddOrUpdatePasskeyAsync(frank, Passkey(), default);
            (await users.UpdateAsync(frank, default)).Succeeded.Should().BeTrue();

            var grace = NewUser("grace", "grace@app.example");
            (await users.CreateAsync(grace, default)).Succeeded.Should().BeTrue();
            var enrol = () => users.AddOrUpdatePasskeyAsync(grace, Passkey(), default);
            await enrol.Should().ThrowAsync<InvalidOperationException>();
        }

        using (var users = new UserStore<SparkUser>(store))
        {
            var frank = (await users.FindByPasskeyIdAsync(credential, default))!;
            frank.Id.Should().Be(frankId);
            (await users.DeleteAsync(frank, default)).Succeeded.Should().BeTrue();
        }

        using (var users = new UserStore<SparkUser>(store))
        {
            (await users.FindByIdAsync(frankId, default)).Should().BeNull();
            (await users.FindByPasskeyIdAsync(credential, default)).Should().BeNull();
            (await users.FindByEmailAsync("FRANK@APP.EXAMPLE", default)).Should().BeNull();

            var heidi = NewUser("heidi", "frank@app.example");
            (await users.CreateAsync(heidi, default)).Succeeded.Should().BeTrue("the deleted user's address is free");
            await users.AddOrUpdatePasskeyAsync(heidi, Passkey(), default);
            (await users.UpdateAsync(heidi, default)).Succeeded.Should().BeTrue("and so is its passkey");
        }
    }

    [Fact]
    public async Task A_user_written_around_the_store_is_found_once_its_keys_are_claimed()
    {
        await using var host = NewHost();
        var store = host.GetService<ISparkStore>();
        var seeded = NewUser("ivan", "ivan@app.example");
        seeded.Logins.Add(new SparkUserLogin { LoginProvider = "GitHub", ProviderKey = "gh-7" });
        string id;
        using (var session = store.OpenSession())
        {
            await session.StoreAsync(seeded);
            await session.SaveChangesAsync();
            id = session.GetId(seeded)!;
        }

        using (var users = new UserStore<SparkUser>(store))
            (await users.FindByLoginAsync("GitHub", "gh-7", default)).Should().BeNull("a raw seed holds no reservation");

        (await SparkUserReservations.ClaimAsync(store, id, seeded)).Should().BeEmpty();

        using (var users = new UserStore<SparkUser>(store))
        {
            (await users.FindByNameAsync("IVAN", default))!.Id.Should().Be(id);
            (await users.FindByEmailAsync("IVAN@APP.EXAMPLE", default))!.Id.Should().Be(id);
            (await users.FindByLoginAsync("GitHub", "gh-7", default))!.Id.Should().Be(id);
        }
    }

    [Fact]
    public async Task Roles_round_trip_on_the_store()
    {
        await using var host = NewHost();
        var store = host.GetService<ISparkStore>();

        using (var roles = new RoleStore(store))
        {
            var role = new SparkRole { Name = "Editors", NormalizedName = "EDITORS" };
            (await roles.CreateAsync(role, default)).Succeeded.Should().BeTrue();
            await roles.AddClaimAsync(role, new Claim("scope", "write"));
            (await roles.UpdateAsync(role, default)).Succeeded.Should().BeTrue();
        }

        using (var roles = new RoleStore(store))
        {
            var role = (await roles.FindByNameAsync("EDITORS", default))!;
            (await roles.GetClaimsAsync(role)).Select(c => c.Value).Should().Equal("write");
            (await roles.FindByIdAsync(role.Id!, default))!.Name.Should().Be("Editors");
            (await roles.DeleteAsync(role, default)).Succeeded.Should().BeTrue();
        }

        using (var roles = new RoleStore(store))
            (await roles.FindByNameAsync("EDITORS", default)).Should().BeNull();
    }
}
