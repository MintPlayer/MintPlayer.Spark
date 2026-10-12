using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Mcp._Infrastructure;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static MintPlayer.Spark.Tests.Mcp._Infrastructure.McpWire;

namespace MintPlayer.Spark.Tests.Mcp;

/// <summary>
/// An agent's principal carries the local user id as <see cref="ClaimTypes.NameIdentifier"/> in every setup (found wiring
/// Fleet, 2026-10-12): application code reads the id from that claim as it does from the sign-in cookie, and Fleet's car
/// row filter treated an authenticated principal without it as a machine, unrestricted, so an agent saw every user's cars.
/// <c>ISparkCurrentUser.Id</c> already mapped the token's <c>sub</c> (M1); code that reads the claim did not.
/// </summary>
/// <remarks>
/// It also carries the role and group claims a cookie sign-in of the user carries (Identity's claims factory), read from
/// the store on every call: application code that tests <c>User.IsInRole</c> treated an agent as having no roles, so a
/// Fleet administrator's agent saw only the administrator's own cars (2026-10-12). A removed role is gone on the next
/// call (S4), while the cookie keeps its claims until Identity revalidates it.
/// </remarks>
public class McpAgentPrincipalTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private const string Reviewers = "Reviewers";

    private string Name(string localPart) => $"{localPart}-{Scope}";

    private static Task<Reply> NameIdAsync(HttpClient client, string token)
        => CallAsync(client, "acme_nameid", new { }, McpAgentClient.Bearer(token));

    [Fact]
    public async Task A_personal_access_token()
    {
        await using var factory = McpAccountHost.Create(Store, mcp => mcp.AddTools<ClaimsProbeTools>());
        var user = await McpAccountHost.CreateUserAsync(factory, Name("pat"));
        var browser = await McpBrowser.SignInAsync(factory.CreateClient(), Name("pat"));
        var (token, _) = await browser.CreateTokenAsync("probe");

        (await NameIdAsync(factory.CreateClient(), token)).ToolText.Should().Contain("nameid=" + user.Id);
    }

    [Fact]
    public async Task An_embedded_servers_token()
    {
        var clientId = $"https://agent.example/{Scope}/probe.json";
        var documents = new McpEmbeddedHost.DocumentServer().Serve(clientId, McpEmbeddedHost.Document(clientId));
        await using var factory = McpEmbeddedHost.Create(Store, documents, mcp => mcp.AddTools<ClaimsProbeTools>());
        var user = await McpAccountHost.CreateUserAsync(factory, Name("embedded"));
        var agent = new McpAgentClient(factory.CreateClient(), clientId, "http://127.0.0.1:53682/callback");
        var tokens = await agent.SignInAndRedeemAsync(user.Email!);

        (await NameIdAsync(factory.CreateClient(), tokens["access_token"]!.GetValue<string>())).ToolText.Should().Contain("nameid=" + user.Id);
    }

    [Fact]
    public async Task An_external_servers_token_carries_the_local_id_never_the_external_subject()
    {
        var issuer = new TestIssuer();
        await using var factory = McpExternalHost.Create(Store, issuer, configureMcp: mcp => mcp.AddTools<ClaimsProbeTools>());
        var user = await McpAccountHost.CreateUserAsync(factory, Name("external"));
        await McpExternalHost.LinkAsync(factory, Store, user.Id!, McpExternalHost.Provider, "corp-" + Name("external"));

        var reply = await NameIdAsync(factory.CreateClient(), issuer.Token("corp-" + Name("external")));

        reply.Status.Should().Be(HttpStatusCode.OK);
        reply.ToolText.Should().Contain("nameid=" + user.Id);
    }

    [Fact]
    public async Task A_personal_access_token_carries_the_cookies_roles_and_loses_a_removed_one_on_the_next_call()
    {
        await using var factory = McpAccountHost.Create(Store, mcp => mcp.AddTools<ClaimsProbeTools>());
        var user = await CreateUserWithRoleAndGroupAsync(factory, Name("pat-roles"));
        var browser = await McpBrowser.SignInAsync(factory.CreateClient(), Name("pat-roles"));
        var (token, _) = await browser.CreateTokenAsync("probe");

        await RolesFollowTheStoreAsync(factory, user, token);
    }

    [Fact]
    public async Task An_embedded_servers_token_carries_the_cookies_roles_and_loses_a_removed_one_on_the_next_call()
    {
        var clientId = $"https://agent.example/{Scope}/roles.json";
        var documents = new McpEmbeddedHost.DocumentServer().Serve(clientId, McpEmbeddedHost.Document(clientId));
        await using var factory = McpEmbeddedHost.Create(Store, documents, mcp => mcp.AddTools<ClaimsProbeTools>());
        var user = await CreateUserWithRoleAndGroupAsync(factory, Name("embedded-roles"));
        var agent = new McpAgentClient(factory.CreateClient(), clientId, "http://127.0.0.1:53682/callback");
        var tokens = await agent.SignInAndRedeemAsync(user.Email!);

        await RolesFollowTheStoreAsync(factory, user, tokens["access_token"]!.GetValue<string>());
    }

    /// <summary>
    /// Setup 2 too, and only the role and group claims: the user's other stored claims stay off the agent's principal. A
    /// stored <c>scope</c> claim copied over would sit beside the token's own and lift D22's cap (<c>mcp:write</c> is read
    /// off the principal).
    /// </summary>
    [Fact]
    public async Task An_external_servers_token_carries_the_cookies_roles_and_none_of_the_users_other_claims()
    {
        var issuer = new TestIssuer();
        await using var factory = McpExternalHost.Create(Store, issuer, configureMcp: mcp => mcp.AddTools<ClaimsProbeTools>());
        var user = await CreateUserWithRoleAndGroupAsync(factory, Name("external-roles"));
        await McpAccountHost.ChangeUserAsync(factory, user.Id!, (users, u) => users.AddClaimAsync(u, new Claim("scope", "mcp:write")));
        await McpExternalHost.LinkAsync(factory, Store, user.Id!, McpExternalHost.Provider, "corp-" + Name("external-roles"));
        var token = issuer.Token("corp-" + Name("external-roles"));

        var scope = await CallAsync(factory.CreateClient(), "acme_scope", new { }, McpAgentClient.Bearer(token));
        scope.ToolText.Should().Be("scope=mcp:read", "the token's scope only, never a claim stored on the user");

        await RolesFollowTheStoreAsync(factory, user, token);
    }

    /// <summary>A user in <see cref="McpAccountHost.Developers"/> (a role) with a stored group claim (<see cref="Reviewers"/>).</summary>
    private static async Task<SparkUser> CreateUserWithRoleAndGroupAsync(SparkEndpointFactory<GuardedContext> factory, string userName)
    {
        var user = await McpAccountHost.CreateUserAsync(factory, userName);
        await McpAccountHost.ChangeUserAsync(factory, user.Id!, (users, u) => users.AddClaimAsync(u, new Claim("group", Reviewers)));
        return user;
    }

    /// <summary>
    /// The agent's role and group claims are the cookie's, and <c>User.IsInRole</c> answers alike, by the name the group
    /// is declared under although <c>UserManager</c> handed the store the normalized one; removing the role (no
    /// security-stamp change, so the token stays valid) takes it off the very next call.
    /// </summary>
    private static async Task RolesFollowTheStoreAsync(SparkEndpointFactory<GuardedContext> factory, SparkUser user, string token)
    {
        const string role = McpAccountHost.Developers;

        var before = await RolesAsync(factory.CreateClient(), token, role);
        before.Status.Should().Be(HttpStatusCode.OK);
        before.ToolText.Should().Be($"inrole=True;claims={await CookieClaimsAsync(factory, user.Id!)}", "the agent carries the roles and groups the user's cookie carries");
        before.ToolText.Should().Contain($"{ClaimTypes.Role}={role}", "the role claim is the declared name");
        before.ToolText.Should().Contain("group=" + Reviewers);

        await McpAccountHost.ChangeUserAsync(factory, user.Id!, (users, u) => users.RemoveFromRoleAsync(u, McpAccountHost.Developers));

        var after = await RolesAsync(factory.CreateClient(), token, role);
        after.Status.Should().Be(HttpStatusCode.OK, "a role change is no security-stamp change: the agent stays signed in");
        after.ToolText.Should().Be($"inrole=False;claims={await CookieClaimsAsync(factory, user.Id!)}", "read from the store on every call (S4)");
        after.ToolText.Should().NotContain(role);
    }

    private static Task<Reply> RolesAsync(HttpClient client, string token, string role)
        => CallAsync(client, "acme_roles", new { role }, McpAgentClient.Bearer(token));

    /// <summary>The role and group claims of the cookie Identity would issue the user now (its claims factory).</summary>
    private static async Task<string> CookieClaimsAsync(SparkEndpointFactory<GuardedContext> factory, string userId)
    {
        using var scope = factory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>();
        var cookie = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<SparkUser>>().CreateAsync((await users.FindByIdAsync(userId))!);
        return ClaimsProbeTools.RoleAndGroupClaims(cookie);
    }
}

/// <summary>Reads the caller the way application code does: claims off the principal.</summary>
[McpServerToolType]
public sealed class ClaimsProbeTools(IHttpContextAccessor accessor)
{
    /// <summary>The claim types core reads groups from, beside the identity's own role claim type.</summary>
    private static readonly string[] GroupClaimTypes =
    [
        "group",
        "groups",
        ClaimTypes.Role,
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid",
        "http://schemas.xmlsoap.org/claims/Group",
    ];

    [McpServerTool(Name = "nameid", ReadOnly = true)]
    public CallToolResult NameId()
        => MintPlayer.Spark.Mcp.SparkMcpResult.Text("nameid=" + accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier));

    [McpServerTool(Name = "roles", ReadOnly = true)]
    public CallToolResult Roles(string role)
    {
        var user = accessor.HttpContext?.User ?? new ClaimsPrincipal();
        return MintPlayer.Spark.Mcp.SparkMcpResult.Text($"inrole={user.IsInRole(role)};claims={RoleAndGroupClaims(user)}");
    }

    [McpServerTool(Name = "scope", ReadOnly = true)]
    public CallToolResult Scope()
        => MintPlayer.Spark.Mcp.SparkMcpResult.Text("scope=" + string.Join(' ', accessor.HttpContext?.User.FindAll("scope").Select(c => c.Value) ?? []));

    /// <summary>The principal's role claims (each identity's role claim type) and group claims, as sorted <c>type=value</c> pairs.</summary>
    public static string RoleAndGroupClaims(ClaimsPrincipal principal)
        => string.Join('|', principal.Identities
            .SelectMany(identity => identity.Claims.Where(c => c.Type == identity.RoleClaimType || GroupClaimTypes.Contains(c.Type, StringComparer.OrdinalIgnoreCase)))
            .Select(c => $"{c.Type}={c.Value}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
}
