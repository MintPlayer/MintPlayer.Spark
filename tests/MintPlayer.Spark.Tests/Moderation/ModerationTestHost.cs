using MintPlayer.Spark.Tests._Infrastructure;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.History;
using MintPlayer.Spark.Moderation;
using MintPlayer.Spark.Moderation.Documents;
using MintPlayer.Spark.Moderation.Services;
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.SoftDelete;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Moderation;

/// <summary>A clock the test moves. Every Moderation rule that involves time reads it.</summary>
public sealed class MoClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// The fixture's security model: a group per earnable privilege (by id, D12) and a Moderators group
/// assigned by claim. Built without the permissive baseline, so every right is the file's.
/// </summary>
public static class MoSecurity
{
    public static readonly Guid Voters = Guid.Parse("46120000-0000-4000-8000-000000000001");
    public static readonly Guid Downvoters = Guid.Parse("46120000-0000-4000-8000-000000000002");
    public static readonly Guid Flaggers = Guid.Parse("46120000-0000-4000-8000-000000000003");
    public static readonly Guid Reviewers = Guid.Parse("46120000-0000-4000-8000-000000000004");
    public static readonly Guid Moderators = Guid.Parse("46120000-0000-4000-8000-000000000005");
    public static readonly Guid Editors = Guid.Parse("46120000-0000-4000-8000-000000000006");
    public const string ModeratorsName = "MoModerators";

    public static SecurityConfiguration Configuration(params (string Resource, Guid Group)[] extra)
    {
        var rights = new List<(string, Guid)>
        {
            ("QueryReadEditNewDelete/MoPost", SparkTestSecurity.AuthenticatedGroupId),
            ("QueryRead/MoPost", SparkTestSecurity.AnonymousGroupId),
            ("EditNewDelete/MoLine", SparkTestSecurity.AuthenticatedGroupId),
            ("Vote/MoPost", Voters),
            ("Downvote/MoPost", Downvoters),
            ("Flag/MoPost", Flaggers),
            ("Review/Moderation", Reviewers),
            ("Edit/MoPost", Editors),
            ("Lock/MoPost", Moderators),
            ("Review/Moderation", Moderators),
            ("Suspend/Moderation", Moderators),
            ("Audit/Moderation", Moderators),
            ("Restore/MoPost", Moderators),
            ("Purge/MoPost", Moderators),
            ("ViewDeleted/MoPost", Moderators),
            ("Revert/MoPost", Moderators),
            ("Revert/MoPost", SparkTestSecurity.AuthenticatedGroupId),
            ("History/MoPost", SparkTestSecurity.AuthenticatedGroupId),
            ("Restore/MoPost", SparkTestSecurity.AuthenticatedGroupId),
            ("Purge/MoPost", SparkTestSecurity.AuthenticatedGroupId),
            ("ViewDeleted/MoPost", SparkTestSecurity.AuthenticatedGroupId),
            ("MoTouch/MoPost", SparkTestSecurity.AuthenticatedGroupId),
            ("Vote/MoPost", Moderators),
            ("Downvote/MoPost", Moderators),
            ("Flag/MoPost", Moderators),
        };
        rights.AddRange(extra);

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
                [Voters.ToString()] = "MoVoters",
                [Downvoters.ToString()] = "MoDownvoters",
                [Flaggers.ToString()] = "MoFlaggers",
                [Reviewers.ToString()] = "MoReviewers",
                [Moderators.ToString()] = ModeratorsName,
                [Editors.ToString()] = "MoEditors",
            },
            Rights = rights.Select((r, i) => new Right
            {
                Id = new Guid($"46129999-0000-4000-8000-{i:D12}"),
                Resource = r.Item1,
                GroupId = r.Item2,
            }).ToList(),
        };
    }

    public static SparkTestSecurity File(params (string Resource, Guid Group)[] extra)
        => SparkTestSecurity.FromJson(JsonSerializer.Serialize(Configuration(extra)));

    /// <summary>Every privilege earnable from day one with no reputation: most tests are not about the gates.</summary>
    public static void OpenPrivileges(SparkModerationOptions o)
    {
        o.Privileges["Upvote"] = new ModerationPrivilegeOptions { GroupId = Voters, Grants = ["Vote"] };
        o.Privileges["Downvote"] = new ModerationPrivilegeOptions { GroupId = Downvoters, Grants = ["Downvote"] };
        o.Privileges["Flag"] = new ModerationPrivilegeOptions { GroupId = Flaggers, Grants = ["Flag"] };
        o.Privileges["Review"] = new ModerationPrivilegeOptions { GroupId = Reviewers, Rep = 1000, Grants = ["Review"] };
        o.Privileges["Edit"] = new ModerationPrivilegeOptions { GroupId = Editors, Rep = 2000, Grants = ["Edit"] };
        // Never at test time.
        o.Jobs.CreditingSchedule = "0 0 31 2 *";
        o.Jobs.DetectorSchedule = "0 0 31 2 *";
        // Eligibility is its own test; elsewhere every voter counts.
        o.Fraud.EligibleVoterMinAgeDays = 0;
        o.Fraud.EligibleVoterMinReputation = 0;
        o.NewAccounts.MaxPostsPerDay = 1000;
    }
}

/// <summary>Sets the request's principal from <c>X-Mo-User</c> / <c>X-Mo-Groups</c> — a signed-in user without Identity.</summary>
internal sealed class MoPrincipalStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue("X-Mo-User", out var user) && user.ToString() is { Length: > 0 } id)
            {
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, id) };
                if (context.Request.Headers.TryGetValue("X-Mo-Groups", out var groups))
                    claims.AddRange(groups.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(g => new Claim("group", g)));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "MoTest"));
            }
            if (context.Request.Headers.TryGetValue("X-Mo-Ip", out var ip))
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
            await nextMiddleware();
        });
        next(app);
    };
}

public sealed class MoHost : IAsyncDisposable
{
    public static readonly Guid PostTypeId = Guid.Parse("46121000-0000-4000-8000-000000000001");
    public static readonly Guid PlainTypeId = Guid.Parse("46121000-0000-4000-8000-000000000002");
    public static readonly Guid LineTypeId = Guid.Parse("46121000-0000-4000-8000-000000000003");
    public static readonly Guid PostsQueryId = Guid.Parse("46121000-0000-4000-8000-000000000011");

    private MoHost(SparkEndpointFactory<MoContext> factory, string cookie, string xsrf, MoClock clock)
    {
        Factory = factory;
        Client = factory.CreateClient();
        Cookie = cookie;
        Xsrf = xsrf;
        Clock = clock;
    }

    public SparkEndpointFactory<MoContext> Factory { get; }
    public HttpClient Client { get; }
    public string Cookie { get; }
    public string Xsrf { get; }
    public MoClock Clock { get; }
    public IDocumentStore Store => Factory.GetService<IDocumentStore>();

    /// <summary>The etag of the stored version, which every update, delete and purge names (#467, D14).</summary>
    public Task<string> EtagAsync(string id) => StoredEtag.OfAsync(Store, id);

    /// <summary>Runs the durable after-commit interceptors of every committed write so far (#482, D17).</summary>
    public Task<int> DrainAsync() => Factory.GetService<TestAfterCommitOutbox>().DrainAsync(Factory.GetService<IServiceProvider>());

    public static async Task<MoHost> StartAsync(
        IDocumentStore store,
        Action<SparkModerationOptions>? configure = null,
        SparkTestSecurity? security = null,
        MoClock? clock = null)
    {
        clock ??= new MoClock();
        var factory = new SparkEndpointFactory<MoContext>(
            store,
            [PostModel(), LineModel(), PlainModel()],
            configureServices: services =>
            {
                services.AddSingleton<IStartupFilter, MoPrincipalStartupFilter>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.AddScoped<MoTouchAction>();
                services.AddScoped<MoPostActions>();
                services.AddSingleton(TestActions.LoaderWithCustom("MoTouch"));
                services.AddScoped<ICustomActionResolver, MoActionResolver>();
                // Moderation's vote reversal is a durable after-commit interceptor (#482, D17); DrainAsync delivers it.
                services.AddTestAfterCommitOutbox();
            },
            configureSpark: spark =>
            {
                spark.AddSoftDelete();
                spark.AddHistory();
                spark.AddModeration<SparkUser>(o =>
                {
                    MoSecurity.OpenPrivileges(o);
                    configure?.Invoke(o);
                });
            },
            security: security ?? MoSecurity.File());
        var (cookie, xsrf) = await factory.MintAntiforgeryAsync();
        return new MoHost(factory, cookie, xsrf, clock);
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(string url, object payload, string? user, params string[] groups)
    {
        var (cookie, xsrf) = await MintAsync(user, groups);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-XSRF-TOKEN", xsrf);
        AddIdentity(request, user, groups);
        var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>The source IP the next requests claim (X-Mo-Ip), for the network-observation tests.</summary>
    public string? RemoteIp { get; set; }

    private readonly Dictionary<string, (string Cookie, string Xsrf)> tokens = new(StringComparer.Ordinal);

    private void AddIdentity(HttpRequestMessage request, string? user, string[] groups)
    {
        if (user is not null)
            request.Headers.Add("X-Mo-User", user);
        if (groups.Length > 0)
            request.Headers.Add("X-Mo-Groups", string.Join(',', groups));
        if (RemoteIp is not null)
            request.Headers.Add("X-Mo-Ip", RemoteIp);
    }

    /// <summary>An antiforgery pair minted for this identity: the token is bound to the user name.</summary>
    private async Task<(string Cookie, string Xsrf)> MintAsync(string? user, string[] groups)
    {
        if (user is null)
            return (Cookie, Xsrf);
        var key = user + "|" + string.Join(',', groups);
        if (tokens.TryGetValue(key, out var cached))
            return cached;

        var warmup = new HttpRequestMessage(HttpMethod.Get, "/spark");
        AddIdentity(warmup, user, groups);
        var response = await Client.SendAsync(warmup);
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
        var pair = (antiforgery + "; XSRF-TOKEN=" + Uri.EscapeDataString(xsrf!), xsrf!);
        tokens[key] = pair;
        return pair;
    }

    public Task<(HttpStatusCode Status, JsonElement Body)> VoteAsync(string user, string postId, int direction)
        => SendAsync("/spark/moderation/vote", Wire.Typed(PostTypeId, new { direction }, postId), user);

    public Task<(HttpStatusCode Status, JsonElement Body)> ModeratorAsync(string url, object payload, string user = "users/mod")
        => SendAsync(url, payload, user, MoSecurity.ModeratorsName);

    /// <summary>Creates a user document as Identity would (fraud measure 10: CreatedAtUtc, RegistrationMethod).</summary>
    public async Task<string> SeedUserAsync(string id, int ageDays = 365, string? email = null, string? registration = "password", DateTime? createdAtUtc = null)
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new SparkUser
        {
            UserName = id.Replace("users/", string.Empty),
            // A domain per user: a shared private domain is a registration-cluster signal.
            Email = email ?? $"{id.Replace("users/", string.Empty)}@{id.Replace("users/", string.Empty)}.example",
            CreatedAtUtc = createdAtUtc ?? Clock.Now.UtcDateTime.AddDays(-ageDays),
            RegistrationMethod = registration,
        }, id);
        await session.SaveChangesAsync();
        return id;
    }

    /// <summary>Stores a post by <paramref name="authorId"/>, as a create through the pipeline would have stamped it.</summary>
    public async Task<string> SeedPostAsync(string authorId, string title = "post", DateTimeOffset? postedAt = null)
    {
        using var session = Store.OpenAsyncSession();
        var post = new MoPost { Title = title, AuthorId = authorId, PostedAt = postedAt ?? Clock.Now };
        await session.StoreAsync(post);
        await session.SaveChangesAsync();
        return post.Id!;
    }

    public async Task<T?> LoadAsync<T>(string id) where T : class
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }

    public async Task<List<ReputationEvent>> EventsForAsync(string userId)
    {
        using var session = Store.OpenAsyncSession();
        return await session.Query<ReputationEvent>()
            .Customize(c => c.WaitForNonStaleResults())
            .Where(e => e.UserId == userId)
            .ToListAsync();
    }

    /// <summary>
    /// Waits until the pending-reputation index has every write made so far (failure bound 60 s).
    /// The badge read waits for it only briefly (2 s, then serves the stale figure, by design), and
    /// on a loaded machine a map-reduce index can take longer than that.
    /// </summary>
    public async Task WaitForPendingReputationIndexAsync()
    {
        using var session = Store.OpenAsyncSession();
        await session.Query<MintPlayer.Spark.Moderation.Indexes.Moderation_PendingReputation.Result, MintPlayer.Spark.Moderation.Indexes.Moderation_PendingReputation>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(60)))
            .Take(1)
            .ToListAsync();
    }

    public async Task<int> CreditAsync()
    {
        using var scope = Factory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ReputationLedger>().CreditDueAsync();
    }

    public async Task<ReputationSummary> SummaryAsync(string userId)
    {
        using var scope = Factory.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ReputationLedger>();
        await ledger.RecomputeAsync([userId]);
        return (await LoadAsync<ReputationSummary>(ModerationIds.Summary(userId)))!;
    }

    internal async Task<FraudDetectionReport> DetectAsync()
    {
        using var scope = Factory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FraudDetector>().RunAsync();
    }

    public static object UpdateBody(string id, string etag, params (string Name, object? Value)[] attributes) => Wire.Typed(PostTypeId, new
    {
        persistentObject = new
        {
            id,
            etag,
            name = "MoPost",
            objectTypeId = PostTypeId.ToString(),
            attributes = attributes.Select(a => new { name = a.Name, value = a.Value, isValueChanged = true }).ToArray(),
        },
    }, id);

    public static object CreateBody(params (string Name, object? Value)[] attributes) => Wire.Typed(PostTypeId, new
    {
        persistentObject = new
        {
            name = "MoPost",
            objectTypeId = PostTypeId.ToString(),
            attributes = attributes.Select(a => new { name = a.Name, value = a.Value, isValueChanged = true }).ToArray(),
        },
    });

    private static EntityTypeFile PostModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = PostTypeId,
            Name = "MoPost",
            ClrType = typeof(MoPost).FullName!,
            Breadcrumb = "{Title}",
            Revisions = new EntityRevisionsDefinition { Enabled = true },
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string", IsVisible = true },
                new() { Id = Guid.NewGuid(), Name = "AuthorId", DataType = "string", IsVisible = true },
                new() { Id = Guid.NewGuid(), Name = "Lines", DataType = "AsDetail", AsDetailType = typeof(MoLine).FullName, IsArray = true, IsVisible = true },
            ],
        },
        Queries = [new SparkQuery { Id = PostsQueryId, Name = "MoPosts", Source = "Database.Posts", EntityType = "MoPost" }],
    };

    private static EntityTypeFile LineModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = LineTypeId,
            Name = "MoLine",
            ClrType = typeof(MoLine).FullName!,
            Attributes = [new() { Id = Guid.NewGuid(), Name = "Text", DataType = "string", IsVisible = true }],
        },
    };

    private static EntityTypeFile PlainModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = PlainTypeId,
            Name = "MoPlain",
            ClrType = typeof(MoPlain).FullName!,
            Attributes = [new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string", IsVisible = true }],
        },
    };

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

public class MoPost : IModeratable, ISoftDeletable
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? AuthorId { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public List<MoLine> Lines { get; set; } = [];
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
}

public class MoLine
{
    public string? Text { get; set; }
}

/// <summary>Not moderatable: a vote on it must be refused exactly like a missing row.</summary>
public class MoPlain
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

public class MoContext : SparkContext
{
    public Raven.Client.Documents.Linq.IRavenQueryable<MoPost> Posts => Session.Query<MoPost>();
    public Raven.Client.Documents.Linq.IRavenQueryable<MoPlain> Plains => Session.Query<MoPlain>();
}

/// <summary>A row rule: a post titled "hidden" is invisible to everyone (the #453 existence test).</summary>
public class MoPostActions(IEntityMapper mapper) : DefaultPersistentObjectActions<MoPost>(mapper)
{
    public override Task<System.Linq.Expressions.Expression<Func<MoPost, bool>>?> GetRowFilterAsync(string action)
        => Task.FromResult<System.Linq.Expressions.Expression<Func<MoPost, bool>>?>(p => p.Title != "hidden");
}

/// <summary>A custom action that saves the selected post through IDatabaseAccess (S-MOD-D).</summary>
public sealed class MoTouchAction(IDatabaseAccess databaseAccess) : ICustomAction
{
    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var po = await databaseAccess.GetPersistentObjectAsync(MoHost.PostTypeId, args.SelectedItems[0].Id!);
        po!["Title"].SetValue("touched");
        await databaseAccess.SavePersistentObjectAsync(po);
    }
}

internal sealed class MoActionResolver(MoTouchAction touch) : ICustomActionResolver
{
    public ICustomAction? Resolve(string name) => string.Equals(name, "MoTouch", StringComparison.OrdinalIgnoreCase) ? touch : null;
    public IReadOnlyList<string> GetRegisteredActionNames() => ["MoTouch"];
}
