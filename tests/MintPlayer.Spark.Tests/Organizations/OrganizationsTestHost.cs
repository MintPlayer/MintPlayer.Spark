using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Organizations;

/// <summary>
/// A Spark host with Authorization and the organizations package (<c>docs/seo_analyzer_PRD.md</c> X11):
/// the package's library rights on, <c>organizations:users</c> bound to every signed-in user, an
/// organization-owned test type <see cref="OgNote"/>, a recording mailer, and a principal taken from the
/// <c>X-Og-User</c> header (a signed-in user id without a cookie). Fixture names start with <c>Og</c>:
/// <c>ActionsResolver</c> matches actions classes by simple name.
/// </summary>
public abstract class OrganizationsTestHost : SparkTestDriver
{
    internal static readonly Guid NoteTypeId = Guid.Parse("0a6e0000-0000-4000-8000-000000000001");
    internal static readonly Guid NotesQueryId = Guid.Parse("0a6e0000-0000-4000-8000-000000000011");
    // The library's stamped ids (UUIDv5 over 'organizations:…', App_Data/Model/*.json).
    internal const string OrganizationTypeId = "87f7591f-42db-5213-821e-e43f8f7115b8";
    internal const string OrganizationsQueryId = "a1f89590-651b-5e70-aedd-0f27ebda695d";
    internal const string MembershipTypeId = "11c2210d-c249-5244-b34c-0028d76782f7";
    internal const string MembersQueryId = "376ec069-4b65-5f4e-a1b6-e822fea9eca7";
    internal const string InvitationTypeId = "0f490455-0e2c-5198-8791-30963ca474a6";
    internal const string InvitationsQueryId = "7154846d-2887-5f68-a1b9-78e2190272cf";
    internal const string PublicBaseUrl = "https://app.test";

    private readonly List<IAsyncDisposable> factories = [];
    private SparkEndpointFactory<OgContext>? factory;

    public override async Task DisposeAsync()
    {
        foreach (var f in factories)
            await f.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>The host, booted on first use.</summary>
    internal SparkEndpointFactory<OgContext> Factory => factory ??= CreateFactory();

    internal OgRecordingMailer Mailer => Factory.GetService<OgRecordingMailer>();

    /// <summary>Whether the host calls <c>AddOrganizations()</c>; the library layers come with the reference either way.</summary>
    internal bool WithPackage { get; set; } = true;

    /// <summary>Additional data handlers a test registers before the host boots.</summary>
    internal List<IOrganizationDataHandler> DataHandlers { get; } = [];

    /// <summary>Live-update options a test sets before the host boots (<c>spark.AddLive</c>).</summary>
    internal Action<MintPlayer.Spark.Live.SparkLiveOptions>? LiveOptions { get; set; }

    /// <summary>More registrations a test adds before the host boots.</summary>
    internal Action<IServiceCollection>? ExtraServices { get; set; }

    /// <summary>More Spark features for a test class built on this host (notifications, quotas, share links).</summary>
    internal Action<MintPlayer.Spark.Abstractions.Builder.ISparkBuilder>? ExtraSpark { get; set; }

    /// <summary>Library aliases whose rights stay on, each with its slot bound to the signed-in group.</summary>
    internal Dictionary<string, string> ExtraLibrarySlots { get; } = [];

    private SparkEndpointFactory<OgContext> CreateFactory()
    {
        var created = new SparkEndpointFactory<OgContext>(
            Store,
            [NoteModel()],
            configureServices: services =>
            {
                services.AddSingleton<IStartupFilter, OgPrincipalStartupFilter>();
                services.AddSingleton<OgRecordingMailer>();
                services.AddSingleton<ISparkMailer>(sp => sp.GetRequiredService<OgRecordingMailer>());
                ExtraServices?.Invoke(services);
                foreach (var handler in DataHandlers)
                    services.AddSingleton<IOrganizationDataHandler>(handler);
            },
            configureSpark: spark =>
            {
                spark.AddAuthentication<SparkUser>(configure: auth =>
                {
                    auth.LocalCredentials = MintPlayer.Spark.Authorization.Configuration.SparkLocalCredentials.SignInOnly;
                    auth.Links.PublicBaseUrl = PublicBaseUrl;
                });
                if (LiveOptions is not null)
                    spark.AddLive(LiveOptions);
                if (WithPackage)
                    spark.AddOrganizations();
                ExtraSpark?.Invoke(spark);
            },
            security: SparkTestSecurity.FromJson(JsonSerializer.Serialize(Security())));
        factories.Add(created);
        return created;
    }

    private SecurityConfiguration Security()
    {
        var libraries = SparkTestSecurity.LibraryRightsOff() ?? [];
        libraries.Remove("organizations");
        foreach (var alias in ExtraLibrarySlots.Keys)
            libraries.Remove(alias);
        var bindings = new Dictionary<string, List<string>> { ["organizations:users"] = ["Signed-in users"] };
        foreach (var slot in ExtraLibrarySlots.Values)
            bindings[slot] = ["Signed-in users"];
        return new SecurityConfiguration
        {
            WellKnown = new Dictionary<string, string>
            {
                [SparkWellKnownGroups.Anonymous] = SparkTestSecurity.AnonymousGroupId.ToString(),
                [SparkWellKnownGroups.Authenticated] = SparkTestSecurity.AuthenticatedGroupId.ToString(),
            },
            Groups = new Dictionary<string, string>
            {
                [SparkTestSecurity.AnonymousGroupId.ToString()] = "Anonymous visitors",
                [SparkTestSecurity.AuthenticatedGroupId.ToString()] = "Signed-in users",
            },
            // Without the package OgNote has no row rule, and startup refuses a type every signed-in user may
            // list without one (VerifyRowPolicyDeclarations): the guard that stands in for the missing policy.
            Rights = WithPackage
                ? [new Right { Key = "notes", Resource = "QueryReadEditNewDelete/OgNote", GroupId = SparkTestSecurity.AuthenticatedGroupId }]
                : [],
            Bindings = bindings,
            Libraries = libraries.Count > 0 ? libraries : null,
        };
    }

    private static EntityTypeFile NoteModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = NoteTypeId,
            Name = "OgNote",
            ClrType = typeof(OgNote).FullName!,
            Breadcrumb = "{Title}",
            // X4: the live-update tests (Live/LiveSocketTests) watch notes; nothing else here notices.
            Live = true,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new() { Id = Guid.NewGuid(), Name = "OrganizationId", DataType = "string" },
            ],
        },
        Queries = [new SparkQuery { Id = NotesQueryId, Name = "OgNotes", Source = "Database.Notes", EntityType = "OgNote", Live = true }],
    };

    // ---- seeding -----------------------------------------------------------------------------------

    /// <summary>A user document, as Identity would store it.</summary>
    internal async Task<SparkUser> SeedUserAsync(string name, bool emailConfirmed = true)
    {
        _ = Factory;
        var user = new SparkUser
        {
            Id = "SparkUsers/" + name,
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            Email = name + "@test.local",
            NormalizedEmail = (name + "@test.local").ToUpperInvariant(),
            EmailConfirmed = emailConfirmed,
            SecurityStamp = Guid.NewGuid().ToString("N"),
        };
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(user, user.Id);
        await session.SaveChangesAsync();
        return user;
    }

    /// <summary>An organization owned by <paramref name="owner"/>, with <paramref name="members"/> in the given roles.</summary>
    internal async Task<Organization> SeedOrganizationAsync(string name, SparkUser owner, params (SparkUser User, OrganizationRole Role)[] members)
    {
        var organization = await Organizations().CreateAsync(name, owner.Id!);
        using var session = Store.OpenAsyncSession();
        foreach (var (user, role) in members)
            await session.StoreAsync(new OrganizationMembership
            {
                Id = OrganizationMembership.IdFor(user.Id!, organization.Id!),
                OrganizationId = organization.Id,
                UserId = user.Id,
                UserName = user.UserName,
                Role = role,
                JoinedAtUtc = DateTime.UtcNow,
            });
        await session.SaveChangesAsync();
        return organization;
    }

    internal async Task<OgNote> SeedNoteAsync(string title, string? organizationId, DateTime? expiresAtUtc = null)
    {
        var note = new OgNote { Title = title, OrganizationId = organizationId, ExpiresAtUtc = expiresAtUtc };
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(note);
        await session.SaveChangesAsync();
        return note;
    }

    /// <summary>A fresh service scope's <see cref="ISparkOrganizations"/> (a new request's cache).</summary>
    internal ISparkOrganizations Organizations() => Factory.CreateScope().ServiceProvider.GetRequiredService<ISparkOrganizations>();

    internal async Task<T?> LoadAsync<T>(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }

    internal async Task<List<OrganizationMembership>> MembersAsync(string organizationId)
    {
        using var session = Store.OpenAsyncSession();
        return await session.Query<OrganizationMembership>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
            .Where(m => m.OrganizationId == organizationId)
            .ToListAsync();
    }

    internal async Task<List<OrganizationInvitation>> InvitationsAsync(string organizationId)
    {
        using var session = Store.OpenAsyncSession();
        return await session.Query<OrganizationInvitation>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
            .Where(i => i.OrganizationId == organizationId)
            .ToListAsync();
    }

    /// <summary>The token and invitation id of the last mail to <paramref name="to"/>, read back from its link.</summary>
    internal (string InvitationId, string Token, SparkMailRequest Mail) LastLink(string to)
    {
        var mail = Mailer.Sent.Last(m => string.Equals(m.To, to, StringComparison.OrdinalIgnoreCase));
        var link = JsonSerializer.SerializeToNode(mail.Data)!["link"]!.GetValue<string>();
        var uri = new Uri(link);
        var token = Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last());
        var id = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query)["id"].ToString();
        return (id, token, mail);
    }

    // ---- HTTP as a user -------------------------------------------------------------------------------

    internal async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(string url, object payload, string? userId)
    {
        using var client = Factory.CreateClient();
        var (cookie, xsrf) = await MintAsync(client, userId);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-XSRF-TOKEN", xsrf);
        if (userId is not null)
            request.Headers.Add(OgPrincipalStartupFilter.Header, userId);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    internal async Task<IReadOnlyList<string>> QueryIdsAsync(JsonNode body, string userId)
    {
        var (status, result) = await SendAsync("/spark/queries/execute", body, userId);
        status.Should().Be(HttpStatusCode.OK);
        return result.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).OrderBy(i => i).ToList();
    }

    /// <summary>An antiforgery pair minted for this identity: the token is bound to the user.</summary>
    internal static async Task<(string Cookie, string Xsrf)> MintAsync(HttpClient client, string? userId)
    {
        var warmup = new HttpRequestMessage(HttpMethod.Get, "/spark");
        if (userId is not null)
            warmup.Headers.Add(OgPrincipalStartupFilter.Header, userId);
        var response = await client.SendAsync(warmup);
        string? antiforgery = null, xsrf = null;
        foreach (var raw in response.Headers.GetValues("Set-Cookie"))
        {
            var nameValue = raw.Split(';', 2)[0];
            var eq = nameValue.IndexOf('=');
            if (eq < 0) continue;
            if (nameValue.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal))
                antiforgery = nameValue;
            else if (nameValue[..eq] == "XSRF-TOKEN")
                xsrf = Uri.UnescapeDataString(nameValue[(eq + 1)..]);
        }
        return (antiforgery + "; XSRF-TOKEN=" + Uri.EscapeDataString(xsrf!), xsrf!);
    }
}

/// <summary>An organization-owned document with the default roles (Viewer reads, Member edits, Admin deletes); it also expires.</summary>
public class OgNote : IOrganizationOwned, IExpiring
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

public class OgContext : SparkContext
{
    public IRavenQueryable<OgNote> Notes => Session.Query<OgNote>();
}

/// <summary>Records every mail instead of queueing it.</summary>
internal sealed class OgRecordingMailer : ISparkMailer
{
    public List<SparkMailRequest> Sent { get; } = [];

    public Task<SparkMailReceipt> SendAsync(SparkMailRequest request, CancellationToken cancellationToken = default)
    {
        lock (Sent)
            Sent.Add(request);
        return Task.FromResult(new SparkMailReceipt(Guid.NewGuid().ToString("N"), false));
    }

    public Task SendCampaignAsync(SparkMailCampaign campaign, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Records what the package asked of an application's organization data.</summary>
internal sealed class OgDataHandler(string name) : IOrganizationDataHandler
{
    public List<string> Deleted { get; } = [];
    public bool Fail { get; set; }

    public string Name => name;

    public Task<object?> ExportAsync(Organization organization, CancellationToken cancellationToken)
        => Task.FromResult<object?>(new { sites = new[] { "https://" + organization.Name.ToLowerInvariant() + ".example" } });

    public Task DeleteAsync(Organization organization, CancellationToken cancellationToken)
    {
        if (Fail)
            throw new InvalidOperationException("the application refused");
        Deleted.Add(organization.Id!);
        return Task.CompletedTask;
    }
}

/// <summary>Sets the request's principal from <c>X-Og-User</c>: a signed-in user id without a cookie.</summary>
internal sealed class OgPrincipalStartupFilter : IStartupFilter
{
    public const string Header = "X-Og-User";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var user) && user.ToString() is { Length: > 0 } id)
            {
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, id) };
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "OgTest"));
            }
            await nextMiddleware();
        });
        next(app);
    };
}
