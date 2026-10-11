using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Dashboards;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Live;
using MintPlayer.Spark.Tests.Settings;

namespace MintPlayer.Spark.Tests.Dashboards;

/// <summary>
/// A dashboard through the real route table and <c>/spark/live</c> (<c>docs/spark_dashboards_PRD.md</c> §9.3, S10's
/// local half): the group bound to <c>dashboards:administrators</c> loads it and follows it, with the providers'
/// values; an anonymous caller and a signed-in non-administrator get Spark's one denial on the load (404) and on the
/// live subscription (<c>gone</c>), and no provider runs for them.
/// </summary>
public class DashboardEndpointTests : SparkTestDriver
{
    private static readonly Guid TypeId = Guid.Parse("5eda5b00-0000-4000-8000-000000000001");
    private const string DatabaseStatisticsTypeId = "81451b51-5162-5c37-af6e-8e682800cafd";
    private static readonly Guid Admins = Guid.Parse("5eda5b00-0000-4000-8000-0000000000a1");
    private static readonly Guid Editors = Guid.Parse("5eda5b00-0000-4000-8000-0000000000e1");
    private const string AdminsName = "TdAdmins";
    private const string EditorsName = "TdEditors";
    private static readonly Uri Server = new("http://localhost/");

    private readonly List<IAsyncDisposable> factories = [];

    public override async Task DisposeAsync()
    {
        foreach (var factory in factories)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static EntityTypeFile Model() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = TypeId,
            Name = "TdDashboard",
            Live = true,
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.Parse("5eda5b00-0000-4000-8000-000000000011"), Name = "Readings", DataType = "number", Order = 1 },
                new EntityAttributeDefinition { Id = Guid.Parse("5eda5b00-0000-4000-8000-000000000012"), Name = "Disk", DataType = "string", Order = 2, Renderer = "spark-meter" },
            ],
        },
    };

    // As an application writes it: the right's groupId is the package's slot, which a typed Right cannot hold.
    private static string Security() => JsonSerializer.Serialize(new
    {
        wellKnown = new Dictionary<string, string>
        {
            [SparkWellKnownGroups.Anonymous] = SparkTestSecurity.AnonymousGroupId.ToString(),
            [SparkWellKnownGroups.Authenticated] = SparkTestSecurity.AuthenticatedGroupId.ToString(),
        },
        groups = new Dictionary<string, string>
        {
            [SparkTestSecurity.AnonymousGroupId.ToString()] = "Anonymous visitors",
            [SparkTestSecurity.AuthenticatedGroupId.ToString()] = "Signed-in users",
            [Admins.ToString()] = AdminsName,
            [Editors.ToString()] = EditorsName,
        },
        // The application's dashboard is granted to the package's slot, as the package's own grant is
        // (Read/DatabaseStatistics).
        bindings = new Dictionary<string, List<string>> { ["dashboards:administrators"] = [AdminsName] },
        rights = new[] { new { key = "td-dashboard", resource = "Read/TdDashboard", groupId = "dashboards:administrators" } },
        libraries = LibrariesExceptDashboards(),
    });

    private static Dictionary<string, bool>? LibrariesExceptDashboards()
    {
        var libraries = SparkTestSecurity.LibraryRightsOff() ?? [];
        libraries.Remove("dashboards");
        return libraries.Count > 0 ? libraries : null;
    }

    private SparkEndpointFactory<StContext> Factory(Action<SparkDashboardsOptions>? options = null)
    {
        TdProvider.Reset();
        var factory = new SparkEndpointFactory<StContext>(
            Store,
            [Model()],
            configureServices: services => services.AddSingleton<IStartupFilter, StPrincipalStartupFilter>(),
            configureSpark: spark => spark.AddDashboards(
                o =>
                {
                    o.Sample = false;
                    options?.Invoke(o);
                },
                dashboards => dashboards.Add("TdDashboard", d => d.Provider<TdProvider>())),
            security: SparkTestSecurity.FromJson(Security()));
        factories.Add(factory);
        return factory;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> LoadAsync(SparkEndpointFactory<StContext> factory, string typeId, string id, string? user, params string[] groups)
    {
        using var client = factory.CreateClient();
        var (cookie, xsrf) = await MintAsync(client, user, groups);
        var request = new HttpRequestMessage(HttpMethod.Post, "/spark/po/load") { Content = JsonContent.Create(Wire.Typed(typeId, id: id)) };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-XSRF-TOKEN", xsrf);
        AddIdentity(request, user, groups);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.IsSuccessStatusCode ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private static void AddIdentity(HttpRequestMessage request, string? user, string[] groups)
    {
        if (user is not null)
            request.Headers.Add("X-St-User", user);
        if (groups.Length > 0)
            request.Headers.Add("X-St-Groups", string.Join(',', groups));
    }

    /// <summary>An antiforgery pair minted for this identity: the token is bound to the user name.</summary>
    private static async Task<(string Cookie, string Xsrf)> MintAsync(HttpClient client, string? user, string[] groups)
    {
        var warmup = new HttpRequestMessage(HttpMethod.Get, "/spark");
        AddIdentity(warmup, user, groups);
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

    private static Task<LiveTestClient> ConnectAsync(SparkEndpointFactory<StContext> factory, string? user, params string[] groups)
        => LiveTestClient.ConnectAsync(factory.CreateWebSocketClient(), Server, request =>
        {
            if (user is not null)
                request.Headers["X-St-User"] = user;
            if (groups.Length > 0)
                request.Headers["X-St-Groups"] = string.Join(',', groups);
        });

    private static JsonElement Value(JsonElement po, string attribute)
        => po.GetProperty("attributes").EnumerateArray().Single(a => a.GetProperty("name").GetString() == attribute).GetProperty("value");

    // ---- load ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_bound_group_loads_the_dashboard_with_the_providers_values()
    {
        var factory = Factory();

        var (status, po) = await LoadAsync(factory, TypeId.ToString(), "main", "users/admin", AdminsName);

        status.Should().Be(HttpStatusCode.OK);
        Value(po, "Readings").GetInt64().Should().Be(1, "the provider's first reading");
        Value(po, "Disk").GetProperty("value").GetDouble().Should().Be(40);
        Value(po, "Disk").GetProperty("max").GetDouble().Should().Be(100);
        po.GetProperty("metadata").GetProperty("dashboard").TryGetProperty("generatedAtUtc", out _).Should().BeTrue("the tile layout recognises a dashboard by it");
        po.GetProperty("breadcrumb").GetString().Should().Be("Td Dashboard", "the type's label (without a translation, its name humanized)");
    }

    [Fact]
    public async Task An_anonymous_caller_and_a_non_administrator_get_404_and_no_provider_runs()
    {
        var factory = Factory();

        var (anonymous, _) = await LoadAsync(factory, TypeId.ToString(), "main", null);
        var (editor, _) = await LoadAsync(factory, TypeId.ToString(), "main", "users/editor", EditorsName);
        var (signedIn, _) = await LoadAsync(factory, TypeId.ToString(), "main", "users/plain");

        anonymous.Should().Be(HttpStatusCode.NotFound);
        editor.Should().Be(HttpStatusCode.NotFound);
        signedIn.Should().Be(HttpStatusCode.NotFound);
        TdProvider.Readings.Should().Be(0, "Spark's Read check runs before the load hook");
    }

    [Fact]
    public async Task Another_object_id_is_404_even_for_the_bound_group()
    {
        var factory = Factory();

        var (status, _) = await LoadAsync(factory, TypeId.ToString(), "other", "users/admin", AdminsName);

        status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_shipped_database_statistics_dashboard_reads_ravendb()
    {
        var factory = Factory();

        var (status, po) = await LoadAsync(factory, DatabaseStatisticsTypeId, "main", "users/admin", AdminsName);
        var (editor, _) = await LoadAsync(factory, DatabaseStatisticsTypeId, "main", "users/editor", EditorsName);

        status.Should().Be(HttpStatusCode.OK);
        Value(po, "DatabaseSize").GetInt64().Should().BeGreaterThan(0);
        Value(po, "DocumentCount").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        Value(po, "Collections").ValueKind.Should().Be(JsonValueKind.Array);
        Value(po, "DatabaseSizeHistory").ValueKind.Should().Be(JsonValueKind.Array, "the history read ran (empty: nothing sampled yet)");
        editor.Should().Be(HttpStatusCode.NotFound, "the package grants only to the slot");
    }

    // ---- live ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_bound_group_follows_the_dashboard_as_the_notifier_pushes_new_values()
    {
        var factory = Factory(o =>
        {
            o.RefreshInterval = TimeSpan.FromMilliseconds(200);
            o.CacheDuration = TimeSpan.Zero;
        });
        await using var client = await ConnectAsync(factory, "users/admin", AdminsName);

        await client.SubscribePoAsync("d", TypeId.ToString(), "main");
        var first = Value((await client.NextAsync("po", "d")).GetProperty("po"), "Readings").GetInt64();

        // The provider counts its readings, so each push changes the value and travels as a patch.
        var patches = 0;
        while (patches < 3)
        {
            var frame = await client.NextAsync(TimeSpan.FromSeconds(10));
            if (frame.GetProperty("sub").GetString() == "d" && frame.GetProperty("type").GetString() is "poPatch" or "po")
                patches++;
        }
        TdProvider.Readings.Should().BeGreaterThan((int)first + 2);
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("users/editor", new[] { EditorsName })]
    public async Task An_anonymous_caller_and_a_non_administrator_get_the_one_live_denial(string? user, string[] groups)
    {
        var factory = Factory(o => o.RefreshInterval = TimeSpan.FromMilliseconds(200));
        await using var client = await ConnectAsync(factory, user, groups);

        await client.SubscribePoAsync("d", TypeId.ToString(), "main");
        var frame = await client.NextAsync();

        frame.GetProperty("sub").GetString().Should().Be("d");
        frame.GetProperty("type").GetString().Should().Be("gone", "forbidden answers as a missing object would (#453)");
        (await client.TryNextAsync(TimeSpan.FromSeconds(1))).Should().BeNull("the subscription ended: the notifier's pushes reach nobody");
        TdProvider.Readings.Should().Be(0);
    }

    // ---- startup ---------------------------------------------------------------------------------

    [Fact]
    public void A_declared_dashboard_without_a_model_type_does_not_start()
    {
        var act = () =>
        {
            var factory = new SparkEndpointFactory<StContext>(
                Store,
                [Model()],
                configureServices: services => services.AddSingleton<IStartupFilter, StPrincipalStartupFilter>(),
                configureSpark: spark => spark.AddDashboards(dashboards => dashboards.Add("TdMissing", d => d.Provider<TdProvider>())),
                security: SparkTestSecurity.FromJson(Security()));
            factories.Add(factory);
        };

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("TdMissing");
    }
}

/// <summary>Counts its readings, so a test sees each one.</summary>
public sealed class TdProvider : IDashboardProvider
{
    private static int readings;

    public static int Readings => Volatile.Read(ref readings);

    public static void Reset() => Interlocked.Exchange(ref readings, 0);

    public Task FillAsync(DashboardValues values, CancellationToken cancellationToken)
    {
        values.Set("Readings", Interlocked.Increment(ref readings));
        values.Set("Disk", DashboardValues.Meter(40, 100));
        values.Set("NotInTheModel", "ignored");
        return Task.CompletedTask;
    }
}

public sealed class TdDashboardActions(IServiceProvider services) : SparkDashboardActions(services);
