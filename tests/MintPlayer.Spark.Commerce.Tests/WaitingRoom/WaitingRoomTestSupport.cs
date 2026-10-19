using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Challenges;
using MintPlayer.Spark.Challenges.Altcha;
using MintPlayer.Spark.Challenges.Deceptive;
using MintPlayer.Spark.Challenges.Risk;
using MintPlayer.Spark.Claims;
using MintPlayer.Spark.WaitingRoom;
using MintPlayer.Spark.WaitingRoom.Admissions;
using MintPlayer.Spark.WaitingRoom.Live;
using MintPlayer.Spark.WaitingRoom.Services;
using MintPlayer.Spark.WaitingRoom.Worker;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Tests.WaitingRoom;

/// <summary>A hand-driven clock: expiry and schedules need no waiting.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;

    public void Set(DateTimeOffset value) => now = value;
}

/// <summary>A visitor as the X1 middleware would set it: a cookie id, a signed-in user, or only an address.</summary>
internal sealed class TestVisitor(string? visitorId, string? userId, string address = "203.0.113.7") : ISparkVisitor
{
    private string? visitor = visitorId;

    public string? VisitorId => visitor;

    public string? UserId => userId;

    public string IdentityKey => userId is not null ? "user:" + userId : visitor is not null ? "visitor:" + visitor : "ip:" + SparkClientAddress.Key(IPAddress.Parse(address));

    public bool HasVisitorCookie => visitor is not null;

    public ValueTask<string> EnsureAsync()
    {
        visitor ??= Guid.NewGuid().ToString("N");
        return ValueTask.FromResult(visitor);
    }
}

/// <summary>Records every publish instead of sending it.</summary>
internal sealed class RecordingTopics : ISparkLiveTopics
{
    public ConcurrentQueue<(string Topic, object Payload)> Public { get; } = new();

    public ConcurrentQueue<(string Identity, string Topic, object Payload)> Private { get; } = new();

    public void Publish(string topic, object payload) => Public.Enqueue((topic, payload));

    public void PublishPrivate(string identityKey, string topic, object payload) => Private.Enqueue((identityKey, topic, payload));
}

/// <summary>A capacity source the test sets.</summary>
internal sealed class TestCapacity : IWaitingRoomCapacity
{
    public WaitingRoomCapacity? Value { get; set; }

    public Task<WaitingRoomCapacity?> GetAsync(string capacityKey, CancellationToken cancellationToken) => Task.FromResult(Value);
}

/// <summary>
/// The waiting room's services over a test database, wired by hand (no web host): a clock, an ephemeral key ring, a
/// recording topic sink and a settable request (<see cref="As"/>).
/// </summary>
internal sealed class WaitingRoomHarness : IDisposable
{
    private readonly ServiceProvider root;
    private readonly HttpContextAccessor accessor = new();

    public WaitingRoomHarness(IDocumentStore store, TestClock clock, Action<SparkWaitingRoomOptions>? configure = null,
        Action<SparkChallengesOptions>? challenges = null, bool withChallenges = false)
    {
        Clock = clock;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(store);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<IHttpContextAccessor>(accessor);
        services.AddSingleton<ISparkClaims>(new SparkClaims(store, clock));
        services.AddSingleton<ISparkLiveTopics>(Topics);
        services.AddSingleton(Capacity);
        services.AddSingleton<IWaitingRoomCapacity>(Capacity);
        services.Configure<SparkWaitingRoomOptions>(o =>
        {
            o.SettleWindow = TimeSpan.Zero;
            configure?.Invoke(o);
        });
        services.Configure<SparkChallengesOptions>(o => challenges?.Invoke(o));
        services.AddSingleton<WaitingRoomTokens>();
        services.AddSingleton<WaitingRoomPublisher>();
        services.AddSingleton<WaitingRoomGateCache>();
        services.AddScoped<SparkWaitingRooms>();
        services.AddScoped<ISparkWaitingRooms>(sp => sp.GetRequiredService<SparkWaitingRooms>());
        services.AddScoped<WaitingRoomAdmissions>();
        services.AddScoped<IWaitingRoomAdmissions>(sp => sp.GetRequiredService<WaitingRoomAdmissions>());
        services.AddScoped<WaitingRoomWorker>();
        if (withChallenges)
        {
            services.AddSingleton(new SparkChallengeProviderRegistry());
            services.AddSingleton<ISparkChallengeSwitch, OptionsChallengeSwitch>();
            services.AddScoped<ISparkHumanChallenges, SparkHumanChallenges>();
            services.AddSingleton<ISparkHumanChallenge, AltchaChallenge>();
            services.AddSingleton<SparkRiskMemory>();
            services.AddSingleton<ISparkRiskDecisionLog, SparkRiskDecisionLog>();
            services.AddScoped<ISparkRiskPipeline, SparkRiskPipeline>();
            services.AddScoped<ISparkDeceptiveMeasures, SparkDeceptiveMeasures>();
            services.AddScoped<ISparkRiskSignal, RecordedEvidenceSignal>();
            services.AddScoped<ISparkRiskSignal, VelocitySignal>();
            services.AddScoped<ISparkRiskSignal, HoneypotSignal>();
            services.AddScoped<ISparkRiskSignal, AutomationFlagSignal>();
            services.AddScoped<ISparkRiskSignal, SharedAddressSignal>();
        }
        root = services.BuildServiceProvider();
    }

    public TestClock Clock { get; }

    public RecordingTopics Topics { get; } = new();

    public TestCapacity Capacity { get; } = new();

    public IServiceProvider Services => root;

    /// <summary>Sets the current request to <paramref name="visitor"/> and answers a scope to resolve request services from.</summary>
    public AsyncServiceScope As(ISparkVisitor visitor, string address = "203.0.113.7")
    {
        var http = new DefaultHttpContext();
        http.Features.Set(visitor);
        http.Connection.RemoteIpAddress = IPAddress.Parse(address);
        var scope = root.CreateAsyncScope();
        http.RequestServices = scope.ServiceProvider;
        accessor.HttpContext = http;
        return scope;
    }

    /// <summary>A worker as one instance (<paramref name="owner"/> names its lease).</summary>
    public WaitingRoomWorker Worker(IServiceProvider scope, string owner = "node-a")
    {
        var worker = ActivatorUtilities.CreateInstance<WaitingRoomWorker>(scope);
        worker.LeaseOwner = owner;
        return worker;
    }

    /// <summary>One worker tick of <paramref name="key"/> as instance <paramref name="owner"/>.</summary>
    public async Task<bool> TickAsync(string key, string owner = "node-a")
    {
        await using var scope = root.CreateAsyncScope();
        var rooms = scope.ServiceProvider.GetRequiredService<ISparkWaitingRooms>();
        var room = (await rooms.GetRoomAsync(key))!.Value.Room;
        return await Worker(scope.ServiceProvider, owner).TickAsync(room, CancellationToken.None);
    }

    /// <summary>The room's stored entries.</summary>
    public async Task<List<WaitingRoomEntry>> EntriesAsync(string key)
    {
        var store = root.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();
        var roomId = WaitingRoomStore.RoomId(key);
        return await session.Query<WaitingRoomEntry>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
            .Where(e => e.RoomId == roomId)
            .Take(10_000)
            .ToListAsync();
    }

    /// <summary>The room's state.</summary>
    public async Task<WaitingRoomState> StateAsync(string key)
    {
        using var session = root.GetRequiredService<IDocumentStore>().OpenAsyncSession();
        return (await session.LoadAsync<WaitingRoomState>(WaitingRoomStore.StateId(key)))!;
    }

    public void Dispose() => root.Dispose();
}
