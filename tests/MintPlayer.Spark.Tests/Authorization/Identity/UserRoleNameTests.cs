using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Authorization.Extensions;
using NSubstitute;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Identity;

/// <summary>
/// A role is a <c>security.json</c> group, and application code tests it by the name the group is declared under:
/// <c>User.IsInRole("Administrators")</c>, an ordinal comparison (Fleet's <c>CarActions</c>). <c>UserManager</c> hands the
/// store the normalized name (<c>ADMINISTRATORS</c>), which Spark's store kept as given, so the cookie's role claim read
/// <c>ADMINISTRATORS</c> and the test was false (found wiring Fleet's agents, 2026-10-12). The store keeps and answers
/// the declared name, a document holding the role in another casing answers by it too, and membership ignores case.
/// </summary>
public class UserRoleNameTests : SparkTestDriver
{
    private const string Administrators = "Administrators";
    private const string Viewers = "Viewers";

    /// <summary>The application's groups, as <c>security.json</c> declares them (core's loader in an app).</summary>
    private static ISecurityConfigurationLoader Declaring(params string[] groups)
    {
        var loader = Substitute.For<ISecurityConfigurationLoader>();
        loader.GetConfiguration().Returns(new SecurityConfiguration
        {
            Groups = groups.ToDictionary(_ => Guid.NewGuid().ToString(), group => group),
        });
        return loader;
    }

    /// <summary>Real Identity, the groups <see cref="Administrators"/> and <see cref="Viewers"/>, and an endpoint that
    /// answers <c>User.IsInRole</c> for the caller's cookie.</summary>
    private static Task<AccountTestHost> StartAsync(IDocumentStore store)
        => AccountTestHost.StartAsync(
            store,
            services: s => s.AddSingleton(Declaring(Administrators, Viewers)),
            endpoints: e => e.MapGet("/test/in-role", (HttpContext http, string role) => http.User.IsInRole(role) ? "in" : "out"));

    private static async Task<string> InRoleAsync(AccountTestHost host, string cookie, string role)
    {
        using var response = await AccountTestHost.SendAsync(host.Client(), HttpMethod.Get, "/test/in-role?role=" + Uri.EscapeDataString(role), cookie);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>A user whose document holds <paramref name="storedRoles"/> as written: as <c>UserManager</c> stored a role
    /// before (the normalized name), or as an operator patches one in.</summary>
    private async Task<string> CreateUserAsync(AccountTestHost host, string userName, params string[] storedRoles)
    {
        var user = await host.CreateUserAsync(userName, userName + "@example.com");
        if (storedRoles.Length > 0)
        {
            using var session = Store.OpenAsyncSession();
            (await session.LoadAsync<SparkUser>(user.Id))!.Roles.AddRange(storedRoles);
            await session.SaveChangesAsync();
        }
        return user.Id!;
    }

    private async Task<List<string>> StoredRolesAsync(string userId)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<SparkUser>(userId))!.Roles;
    }

    /// <summary>Runs <paramref name="action"/> through <see cref="UserManager{TUser}"/>, as the application would.</summary>
    private static Task<T> WithUserAsync<T>(AccountTestHost host, string userId, Func<UserManager<SparkUser>, SparkUser, Task<T>> action)
        => host.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            return await action(users, (await users.FindByIdAsync(userId))!);
        });

    [Fact]
    public async Task A_role_added_through_UserManager_is_kept_and_answered_by_its_declared_name()
    {
        await using var host = await StartAsync(Store);
        var userId = await CreateUserAsync(host, "ada");

        // The caller's casing does not matter: UserManager normalizes the name before the store sees it.
        (await WithUserAsync(host, userId, (users, u) => users.AddToRoleAsync(u, "administrators"))).Succeeded.Should().BeTrue();
        (await WithUserAsync(host, userId, (users, u) => users.AddToRoleAsync(u, "Undeclared"))).Succeeded.Should().BeTrue();

        (await StoredRolesAsync(userId)).Should().Equal(new[] { Administrators, "UNDECLARED" },
            "the store keeps the name the group is declared under, and a name no group declares as it was handed over");
        await WaitForIndexesAsync();
        var cookie = await host.CookieSignInAsync(host.Client(), "ada@example.com");
        (await InRoleAsync(host, cookie, Administrators)).Should().Be("in", "the cookie's role claim is the declared name");
    }

    [Fact]
    public async Task A_document_holding_the_role_in_another_casing_answers_by_the_declared_name()
    {
        await using var host = await StartAsync(Store);
        var userId = await CreateUserAsync(host, "bea", "ADMINISTRATORS", "viewers", "VIEWERS");
        await WaitForIndexesAsync();

        var cookie = await host.CookieSignInAsync(host.Client(), "bea@example.com");

        (await InRoleAsync(host, cookie, Administrators)).Should().Be("in", "a document UserManager wrote before reads by the declared name");
        (await InRoleAsync(host, cookie, Viewers)).Should().Be("in", "and so does one patched by hand");
        (await WithUserAsync(host, userId, (users, u) => users.GetRolesAsync(u))).Should().Equal(Administrators, Viewers);
        (await StoredRolesAsync(userId)).Should().Equal(new[] { "ADMINISTRATORS", "viewers", "VIEWERS" }, "reading rewrites nothing");
    }

    [Theory]
    [InlineData("ADMINISTRATORS")]
    [InlineData("administrators")]
    [InlineData(Administrators)]
    public async Task UserManager_removes_a_role_the_document_holds_in_any_casing(string stored)
    {
        await using var host = await StartAsync(Store);
        var userId = await CreateUserAsync(host, "cy", stored, Viewers);

        (await WithUserAsync(host, userId, (users, u) => users.IsInRoleAsync(u, Administrators))).Should().BeTrue();
        (await WithUserAsync(host, userId, (users, u) => users.RemoveFromRoleAsync(u, Administrators))).Succeeded.Should().BeTrue();

        (await StoredRolesAsync(userId)).Should().Equal(Viewers);
    }

    [Fact]
    public async Task The_users_in_a_role_are_found_whichever_casing_they_hold_it_in_and_it_is_asked_in()
    {
        await using var host = await StartAsync(Store);
        var holders = new[]
        {
            await CreateUserAsync(host, "dee", Administrators),
            await CreateUserAsync(host, "eve", "ADMINISTRATORS"),
            await CreateUserAsync(host, "fay", "administrators"),
        }.Order(StringComparer.Ordinal).ToList();
        await CreateUserAsync(host, "gus", Viewers);
        await WaitForIndexesAsync();

        await host.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            Ids(await users.GetUsersInRoleAsync(Administrators)).Should().Equal(holders, "UserManager asks with the normalized name");

            // The store itself, asked in each casing.
            var store = (IUserRoleStore<SparkUser>)sp.GetRequiredService<IUserStore<SparkUser>>();
            foreach (var asked in new[] { Administrators, "ADMINISTRATORS", "administrators" })
                Ids(await store.GetUsersInRoleAsync(asked, CancellationToken.None)).Should().Equal(holders, "asked as {0}", asked);
            return 0;
        });

        static List<string> Ids(IEnumerable<SparkUser> found) => found.Select(u => u.Id!).Order(StringComparer.Ordinal).ToList();
    }
}
