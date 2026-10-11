using System.Net;
using System.Text;
using System.Text.Json;
using CodeCoverage.CommitGraph;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Tests.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Tests.CommitGraph;

/// <summary>
/// The <c>coverage-git</c> sidecar, in memory, behind the real <see cref="CommitGraphSidecarClient"/>:
/// it answers the contract's four routes and records every request, so a test can assert which
/// mirrors were fetched or deleted without a container or a network.
/// </summary>
public sealed class FakeSidecar : HttpMessageHandler
{
    public const string BaseUrl = "http://coverage-git.test/";

    /// <summary><c>{provider}/{repoId}</c> of every mirror that exists.</summary>
    public HashSet<string> Mirrors { get; } = [];

    /// <summary>The size a fetch reports per mirror; 10 bytes when not listed.</summary>
    public Dictionary<string, long> Sizes { get; } = [];

    /// <summary>When set, answers a fetch instead of the default (cloned/fetched).</summary>
    public Func<string, SidecarFetchResult?>? OnFetch { get; set; }

    /// <summary>The page each mirror serves.</summary>
    public Dictionary<string, SidecarGraphPage> Pages { get; } = [];

    /// <summary>Every request as <c>METHOD provider/repoId[/route]</c>, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Every fetch body, raw.</summary>
    public List<string> FetchBodies { get; } = [];

    public IReadOnlyList<string> Fetches => [.. Requests.Where(r => r.StartsWith("POST ")).Select(r => r["POST ".Length..^"/fetch".Length])];
    public IReadOnlyList<string> Deletes => [.. Requests.Where(r => r.StartsWith("DELETE ")).Select(r => r["DELETE ".Length..])];
    public IReadOnlyList<string> GraphReads => [.. Requests.Where(r => r.StartsWith("GET "))];

    /// <summary>The mirror volume <c>GET /stats</c> reports.</summary>
    public SidecarStats Stats { get; set; } = new(FreeBytes: 600, TotalBytes: 1_000, MirrorCount: 0, MirrorBytes: 0);

    /// <summary>When true, every request fails as a connection to a stopped container would.</summary>
    public bool Down { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
            throw new HttpRequestException("Connection refused (coverage-git.test:80)");

        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        if (request.Method == HttpMethod.Get && path == "stats")
        {
            Requests.Add("GET stats");
            return Json(HttpStatusCode.OK, Stats);
        }
        if (!path.StartsWith("repos/")) return new HttpResponseMessage(HttpStatusCode.NotFound);
        path = path["repos/".Length..];
        Requests.Add(request.Method == HttpMethod.Get ? $"GET {path}{request.RequestUri.Query}" : $"{request.Method} {path}");

        if (request.Method == HttpMethod.Delete)
        {
            Mirrors.Remove(path);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/fetch"))
        {
            var mirror = path[..^"/fetch".Length];
            FetchBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var existed = Mirrors.Contains(mirror);
            var result = OnFetch?.Invoke(mirror) ?? new SidecarFetchResult(
                existed ? "fetched" : "cloned", null, Sizes.GetValueOrDefault(mirror, 10), "refs-hash", 3, 1_000_000, 5);
            if (result.Succeeded) Mirrors.Add(mirror);
            return Json(HttpStatusCode.OK, result);
        }

        if (request.Method == HttpMethod.Get && path.EndsWith("/graph"))
        {
            var mirror = path[..^"/graph".Length];
            if (!Mirrors.Contains(mirror)) return Json(HttpStatusCode.NotFound, new { state = "missing" });
            var page = Pages.GetValueOrDefault(mirror) ?? Page();
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            if (query["snapshot"] is { } snapshot && snapshot != page.SnapshotId)
                return Json(HttpStatusCode.Gone, new { state = "stale", snapshotId = page.SnapshotId });
            return Json(HttpStatusCode.OK, page);
        }

        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json"),
    };

    /// <summary>A page of linear history, one row per author email (fixture identities only).</summary>
    public static SidecarGraphPage Page(params (string Sha, string? Email)[] rows)
    {
        var list = rows.Select((r, i) => new SidecarRow(
            r.Sha, i + 1 < rows.Length ? [rows[i + 1].Sha] : [], "Test Author", r.Email,
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(-i), $"Commit number {i}",
            0, 0, i == 0 ? [] : [0], i + 1 < rows.Length ? [[0, 0]] : [])).ToList();
        return new SidecarGraphPage("snap-1", "refs-hash", list.Count, 0, 1, [], list,
            list.Count == 0 ? [] : [new SidecarRef("refs/heads/main", "head", list[0].Sha)]);
    }
}

/// <summary>A clock a test moves by hand.</summary>
public sealed class MutableTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Step 4 of the avatar order, counted: each call is one forge request.</summary>
public sealed class CountingAvatarResolver : IForgeAvatarResolver
{
    public EForgeProvider Provider => EForgeProvider.GitHub;

    /// <summary>The answer per email; an email not listed is an author with no account.</summary>
    public Dictionary<string, string?> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<IReadOnlyList<AvatarLookup>> Calls { get; } = [];

    public Task<IReadOnlyDictionary<string, string?>?> ResolveAsync(Repository repository, IReadOnlyList<AvatarLookup> lookups, CancellationToken cancellationToken = default)
    {
        Calls.Add(lookups);
        IReadOnlyDictionary<string, string?> answer = lookups.ToDictionary(l => l.Email, l => Answers.GetValueOrDefault(l.Email), StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyDictionary<string, string?>?>(answer);
    }
}

/// <summary>Serves avatar images and counts the fetches.</summary>
public sealed class StubImages : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    /// <summary>The content type served; an SVG must be refused.</summary>
    public string ContentType { get; set; } = "image/png";

    public byte[] Bytes { get; set; } = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        var content = new ByteArrayContent(Bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ContentType);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}

/// <summary>Everything the commit-graph services need, wired over one test store.</summary>
public sealed class CommitGraphKit
{
    public FakeSidecar Sidecar { get; } = new();
    public RecordingMessageBus Bus { get; } = new();
    public MutableTime Time { get; } = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    public CountingAvatarResolver Avatars { get; } = new();
    public StubImages Images { get; } = new();
    public ScriptedDiffService Forge { get; }
    public CommitGraphOptions Options { get; } = new() { SidecarUrl = FakeSidecar.BaseUrl, AvatarKeySecret = "test-avatar-key" };
    public IServiceProvider Services { get; }

    public CommitGraphKit(IDocumentStore store, ScriptedDiffService? forge = null, Action<IServiceCollection>? extra = null)
    {
        Forge = forge ?? new ScriptedDiffService();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(store);
        services.AddScoped<IAsyncDocumentSession>(_ => store.OpenAsyncSession());
        services.AddSingleton<IMessageBus>(Bus);
        services.AddSingleton<IForgeIntegration>(Forge);
        services.AddSingleton<IForgeIntegrationResolver>(Forge);
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IOptions<CommitGraphOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddSingleton<ICommitGraphSidecar>(sp => new CommitGraphSidecarClient(
            new HttpClient(Sidecar, disposeHandler: false) { BaseAddress = Options.SidecarUrl is null ? null : new Uri(Options.SidecarUrl) },
            sp.GetRequiredService<IOptions<CommitGraphOptions>>()));
        services.AddHttpClient(CommitGraphAvatars.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new NonDisposing(Images));
        services.AddSingleton<IForgeAvatarResolver>(Avatars);
        services.AddScoped<ICommitGraphMirrors, CommitGraphMirrors>();
        services.AddScoped<ICommitGraphReader, CommitGraphReader>();
        services.AddScoped<ICommitGraphAvatars, CommitGraphAvatars>();
        extra?.Invoke(services);
        Services = services.BuildServiceProvider();
    }

    /// <summary>A fresh scope's service, as a request or a message handler gets one.</summary>
    public T Get<T>() where T : notnull => Services.CreateScope().ServiceProvider.GetRequiredService<T>();

    /// <summary>Handles every queued commit-graph message (and the ones they queue), as the messaging host would.</summary>
    public async Task DrainAsync()
    {
        for (var guard = 0; guard < 100; guard++)
        {
            var next = Bus.Messages.FirstOrDefault(m => m is FetchCommitGraphMessage or DeleteCommitGraphMirrorMessage);
            if (next is null) return;
            Bus.Messages.Remove(next);
            var mirrors = Get<ICommitGraphMirrors>();
            switch (next)
            {
                case FetchCommitGraphMessage fetch:
                    await new FetchCommitGraphRecipient(mirrors).HandleAsync(fetch);
                    break;
                case DeleteCommitGraphMirrorMessage delete:
                    await new DeleteCommitGraphMirrorRecipient(mirrors).HandleAsync(delete);
                    break;
            }
        }
    }

    /// <summary>Seeds a mirror the app and the sidecar both know about.</summary>
    public static async Task SeedMirrorAsync(IDocumentStore store, FakeSidecar sidecar, long repoId, long size, DateTime? viewedAtUtc,
        string? installation = "github:1", DateTime? fetchedAtUtc = null)
    {
        using var session = store.OpenAsyncSession();
        var repositoryId = Repository.DocumentId(EForgeProvider.GitHub, repoId);
        await session.StoreAsync(new RepositoryGraphState
        {
            Repository = repositoryId,
            HasMirror = true,
            Outcome = "cloned",
            SizeBytes = size,
            LastViewedAtUtc = viewedAtUtc,
            LastFetchAtUtc = fetchedAtUtc ?? viewedAtUtc,
            InstallationId = installation,
        }, RepositoryGraphState.ForRepository(repositoryId));
        await session.SaveChangesAsync();
        sidecar.Mirrors.Add($"github/{repoId}");
    }

    /// <summary>Seeds a connected repository (public unless said otherwise).</summary>
    public static async Task SeedRepositoryAsync(IDocumentStore store, long repoId, string owner = "acme", string? name = null, bool isPrivate = false)
    {
        using var session = store.OpenAsyncSession();
        name ??= $"repo{repoId}";
        await session.StoreAsync(new Repository
        {
            GitHubId = repoId,
            Provider = EForgeProvider.GitHub,
            Name = name,
            FullName = $"{owner}/{name}",
            OwnerLogin = owner,
            IsPrivate = isPrivate,
        }, Repository.DocumentId(EForgeProvider.GitHub, repoId));
        await session.SaveChangesAsync();
    }

    /// <summary>The factory disposes its handlers; the test owns this one.</summary>
    private sealed class NonDisposing(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing) { }
    }
}
