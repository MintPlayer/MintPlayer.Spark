using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CodeCoverage.CommitGraph;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using CodeCoverage.Tests._Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.CommitGraph;

/// <summary>
/// The host metrics dashboard and the mirrors list (#504, plan M5b) through the real composition root: only the
/// Administrators group, which a forge account joins by its stable user id in <c>Coverage:Administrators</c>, sees
/// either; anyone else gets Spark's one answer for a missing object, on the load, on the live subscription and on the
/// query; and the mirror figures survive a sidecar that does not answer.
/// </summary>
[Collection(CoverageWebHostCollection.Name)]
public class HostMetricsDashboardTests(HostMetricsHostFixture host) : IClassFixture<HostMetricsHostFixture>
{
    private const string HostMetricsTypeId = "c0e5da5b-0000-4000-8000-000000000001";
    private const string MirrorsQueryId = "56276334-7a42-4052-bac4-b755f72b3b49";

    /// <summary>A type and a query this application does not have: the answer every refusal must equal.</summary>
    private const string UnknownTypeId = "c0e5da5b-0000-4000-8000-0000000000ff";
    private const string UnknownQueryId = "c0e5da5b-0000-4000-8000-0000000000fe";

    public static TheoryData<string?> Outsiders => new() { null, HostMetricsHostFixture.MemberUserId };

    [Theory]
    [MemberData(nameof(Outsiders))]
    public async Task An_anonymous_caller_and_a_non_administrator_get_the_standard_denial_on_the_load(string? user)
    {
        var dashboard = await host.LoadAsync(HostMetricsTypeId, HostMetricsActions.ObjectId, user);
        var unknown = await host.LoadAsync(UnknownTypeId, HostMetricsActions.ObjectId, user);

        dashboard.Status.Should().NotBe(HttpStatusCode.OK);
        (dashboard.Status, dashboard.Body).Should().Be((unknown.Status, unknown.Body),
            "the dashboard must be indistinguishable from a type that does not exist (#453)");
    }

    [Theory]
    [MemberData(nameof(Outsiders))]
    public async Task An_anonymous_caller_and_a_non_administrator_get_the_one_live_denial(string? user)
    {
        await using var socket = await host.ConnectLiveAsync(user);

        var frame = await socket.SubscribeAsync(HostMetricsTypeId, HostMetricsActions.ObjectId);

        frame.GetProperty("type").GetString().Should().Be("gone", "forbidden answers as a missing object would (#453)");
    }

    [Theory]
    [MemberData(nameof(Outsiders))]
    public async Task An_anonymous_caller_and_a_non_administrator_get_the_standard_denial_on_the_mirrors_query(string? user)
    {
        var mirrors = await host.ExecuteQueryAsync(MirrorsQueryId, user);
        var unknown = await host.ExecuteQueryAsync(UnknownQueryId, user);

        // The refusal echoes the id that was asked for; apart from that it must be the same answer.
        mirrors.Status.Should().NotBe(HttpStatusCode.OK);
        (mirrors.Status, mirrors.Body.Replace(MirrorsQueryId, "{id}")).Should().Be((unknown.Status, unknown.Body.Replace(UnknownQueryId, "{id}")),
            "the mirrors list must be indistinguishable from a query that does not exist");
    }

    [Fact]
    public async Task The_administrator_seeded_by_forge_id_reads_the_host_and_mirror_figures()
    {
        host.Sidecar.Down = false;

        var (status, body) = await host.LoadAsync(HostMetricsTypeId, HostMetricsActions.ObjectId, HostMetricsHostFixture.AdminUserId);

        status.Should().Be(HttpStatusCode.OK, body);
        using var po = JsonDocument.Parse(body);
        Value(po, "MirrorCount").GetInt64().Should().Be(2, "the state without a mirror does not count");
        Value(po, "MirrorBytes").GetInt64().Should().Be(350);
        Value(po, "MirrorEvictions").GetInt64().Should().Be(3);
        Value(po, "MirrorRefusals").GetInt64().Should().Be(4);
        Value(po, "MirrorVolumeFree").GetInt64().Should().Be(600);
        var volume = Value(po, "MirrorVolume");
        volume.GetProperty("value").GetDouble().Should().Be(400);
        volume.GetProperty("max").GetDouble().Should().Be(1_000);
        Value(po, "HostDisk").GetProperty("max").GetDouble().Should().BeGreaterThan(0, "the package's host metrics are on the page");
        Value(po, "DocumentCount").GetInt64().Should().BeGreaterThan(0, "the package's RavenDB statistics are on the page");
    }

    [Fact]
    public async Task The_administrator_follows_the_dashboard_live()
    {
        host.Sidecar.Down = false;
        await using var socket = await host.ConnectLiveAsync(HostMetricsHostFixture.AdminUserId);

        var frame = await socket.SubscribeAsync(HostMetricsTypeId, HostMetricsActions.ObjectId);

        frame.GetProperty("type").GetString().Should().Be("po");
    }

    [Fact]
    public async Task The_mirror_figures_survive_a_sidecar_that_does_not_answer()
    {
        host.Sidecar.Down = true;
        try
        {
            var (status, body) = await host.LoadAsync(HostMetricsTypeId, HostMetricsActions.ObjectId, HostMetricsHostFixture.AdminUserId);

            status.Should().Be(HttpStatusCode.OK, body);
            using var po = JsonDocument.Parse(body);
            Value(po, "MirrorCount").GetInt64().Should().Be(2, "the app's own figures do not depend on the sidecar");
            Value(po, "MirrorVolume").ValueKind.Should().Be(JsonValueKind.Null, "the sidecar's values stay empty");
            Value(po, "MirrorVolumeFree").ValueKind.Should().Be(JsonValueKind.Null);
            body.Should().NotContain("Connection refused", "no exception text reaches a value (§9.3)");
        }
        finally
        {
            host.Sidecar.Down = false;
        }
    }

    [Fact]
    public async Task The_administrator_lists_the_mirrors_with_their_size()
    {
        var (status, body) = await host.ExecuteQueryAsync(MirrorsQueryId, HostMetricsHostFixture.AdminUserId);

        status.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain(Repository.DocumentId(EForgeProvider.GitHub, 7101));
        body.Should().Contain("SizeBytes");
        body.Should().Contain("250");
    }

    [Fact]
    public void Only_a_configured_forge_user_id_counts_never_a_login()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{CoverageAdministrators.Section}:0"] = "github:424242",
            [$"{CoverageAdministrators.Section}:1"] = "github:somebody",
            [$"{CoverageAdministrators.Section}:2"] = "nosuchforge:1",
            [$"{CoverageAdministrators.Section}:3"] = "gitlab:77",
        }).Build();

        var configured = CoverageAdministrators.Configured(configuration);

        configured.SetEquals([(EForgeProvider.GitHub, "424242"), (EForgeProvider.GitLab, "77")]).Should().BeTrue(
            string.Join(", ", configured));
    }

    private static JsonElement Value(JsonDocument po, string attribute)
        => po.RootElement.GetProperty("attributes").EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == attribute).GetProperty("value");
}

/// <summary>
/// One host for <see cref="HostMetricsDashboardTests"/>: the real application with the sidecar replaced by
/// <see cref="FakeSidecar"/>, a header-driven test principal (the user id only; groups come from the app's own
/// membership), no dashboard cache, no sampler, and two seeded users told apart only by their GitHub user id.
/// </summary>
public sealed class HostMetricsHostFixture : CoverageRavenTest, IAsyncLifetime
{
    public const string AdminUserId = "SparkUsers/hostmetrics-admin";
    public const string MemberUserId = "SparkUsers/hostmetrics-member";
    private const string AdminGitHubId = "424242";
    private const string MemberGitHubId = "515151";
    private const string UserHeader = "X-Test-User";

    public IDocumentStore Store { get; private set; } = null!;
    public CoverageWebAppFactory Factory { get; private set; } = null!;
    public FakeSidecar Sidecar { get; } = new() { Stats = new(FreeBytes: 600, TotalBytes: 1_000, MirrorCount: 2, MirrorBytes: 350) };

    public async Task InitializeAsync()
    {
        Store = GetDocumentStore();
        Factory = new CoverageWebAppFactory(Store, services =>
        {
            services.AddSingleton<ICommitGraphSidecar>(sp => new CommitGraphSidecarClient(
                new HttpClient(Sidecar, disposeHandler: false) { BaseAddress = new Uri(FakeSidecar.BaseUrl) },
                sp.GetRequiredService<IOptions<CommitGraphOptions>>()));
            services.AddSingleton<IStartupFilter, TestPrincipalStartupFilter>();
        }, new Dictionary<string, string?>
        {
            [$"{CoverageAdministrators.Section}:0"] = $"github:{AdminGitHubId}",
            ["Spark:Dashboards:CacheDuration"] = "00:00:00",
            ["Spark:Dashboards:Sample"] = "false",
        });
        _ = Factory.CreateClient();

        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(User("hostmetrics-admin", AdminGitHubId), AdminUserId);
            await session.StoreAsync(User("hostmetrics-member", MemberGitHubId), MemberUserId);
            await SeedStateAsync(session, 7101, hasMirror: true, size: 250);
            await SeedStateAsync(session, 7102, hasMirror: true, size: 100);
            await SeedStateAsync(session, 7103, hasMirror: false, size: 0);
            await session.StoreAsync(new CommitGraphMetrics { Evictions = 3, Refusals = 4 }, CommitGraphMetrics.DocumentId);
            await session.SaveChangesAsync();
        }
        WaitForIndexing(Store);
    }

    private static SparkUser User(string name, string gitHubId) => new()
    {
        UserName = name,
        NormalizedUserName = name.ToUpperInvariant(),
        Logins = [new SparkUserLogin { LoginProvider = "GitHub", ProviderKey = gitHubId, ProviderDisplayName = "GitHub" }],
    };

    private static Task SeedStateAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, long repoId, bool hasMirror, long size)
    {
        var repository = Repository.DocumentId(EForgeProvider.GitHub, repoId);
        return session.StoreAsync(new RepositoryGraphState { Repository = repository, HasMirror = hasMirror, SizeBytes = size },
            RepositoryGraphState.ForRepository(repository));
    }

    public async Task<(HttpStatusCode Status, string Body)> LoadAsync(string typeId, string id, string? user)
        => await PostAsync("/spark/po/load", new { objectTypeId = typeId, id }, user);

    public async Task<(HttpStatusCode Status, string Body)> ExecuteQueryAsync(string queryId, string? user)
        => await PostAsync("/spark/queries/execute", new { queryId }, user);

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string path, object body, string? user)
    {
        using var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (user is not null)
            request.Headers.Add(UserHeader, user);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public async Task<LiveSocket> ConnectLiveAsync(string? user)
    {
        var client = Factory.Server.CreateWebSocketClient();
        if (user is not null)
            client.ConfigureRequest = request => request.Headers[UserHeader] = user;
        return new LiveSocket(await client.ConnectAsync(new Uri("http://localhost/spark/live"), CancellationToken.None));
    }

    public new async Task DisposeAsync()
    {
        try { await Factory.DisposeAsync(); }
        catch (Exception ex) { Console.WriteLine($"[HostMetricsHostFixture] host disposal faulted: {ex}"); }

        try { Store?.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[HostMetricsHostFixture] store disposal faulted: {ex}"); }

        try { base.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[HostMetricsHostFixture] RavenDB test-driver disposal faulted: {ex}"); }
    }

    /// <summary>Signs the request in as the user id in <see cref="UserHeader"/>; no group claims.</summary>
    private sealed class TestPrincipalStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(UserHeader, out var user) && user.ToString() is { Length: > 0 } id)
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, id)], "HostMetricsTest"));
                }
                await nextMiddleware();
            });
            next(app);
        };
    }
}

/// <summary>A <c>/spark/live</c> socket: subscribes to one object and reads its first frame, skipping pings.</summary>
public sealed class LiveSocket(WebSocket socket) : IAsyncDisposable
{
    public async Task<JsonElement> SubscribeAsync(string objectTypeId, string id)
    {
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { op = "subscribe", sub = "d", po = new { objectTypeId, id } }),
            WebSocketMessageType.Text, true, CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var buffer = new byte[256 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, timeout.Token);
            if (received.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException($"The live socket closed ({socket.CloseStatus}) before answering.");
            message.Write(buffer, 0, received.Count);
            if (!received.EndOfMessage) continue;

            var frame = JsonDocument.Parse(message.ToArray()).RootElement.Clone();
            message.SetLength(0);
            if (frame.GetProperty("type").GetString() != "ping" && frame.TryGetProperty("sub", out var sub) && sub.GetString() == "d")
                return frame;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
        }
        catch
        {
            // The server may already have closed it.
        }
        socket.Dispose();
    }
}
