using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Authorization.OneShotOAuth;

/// <summary>
/// The in-memory hand-off between the OAuth callback and the action's pass 2 (#554 f1): one entry per
/// authorization attempt, bound to its user, alive for <see cref="Lifetime"/>, taken at most once.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Not <c>IMemoryCache</c>, deliberately.</b> An OAuth app token does not expire on its own, so a
/// token nobody takes (the user closed the tab after consenting) must be <em>revoked</em>, not merely
/// forgotten; <c>IMemoryCache</c> evicts lazily, on a later access, and may never call back. Here each
/// attempt has its own timer that removes it at the end of its lifetime and revokes any token in it.
/// A <see cref="ConcurrentDictionary{TKey, TValue}"/> also makes the take atomic: two concurrent
/// pass 2s cannot both get the token.
/// </para>
/// <para>
/// One node only: the token never leaves this process. That fits the single VPS the apps run on; a
/// farm would need the callback and pass 2 on the same node.
/// </para>
/// </remarks>
internal sealed partial class SparkOneShotOAuthStore : IDisposable
{
    /// <summary>How long an attempt, and the token it may hold, lives.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>The named <see cref="HttpClient"/> used for the token exchange and the revocation.</summary>
    internal const string HttpClientName = "MintPlayer.Spark.OneShotOAuth";

    private readonly ConcurrentDictionary<string, Attempt> attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> revocations = new();
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly IHttpClientFactory httpClients;
    [Inject] private readonly IOptions<SparkOneShotOAuthOptions> options;
    [Inject] private readonly ILogger<SparkOneShotOAuthStore> logger;

    internal DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>Records a new attempt and starts its lifetime.</summary>
    public void Add(Attempt attempt)
    {
        attempts[attempt.Handle] = attempt;
        attempt.Timer = clock.CreateTimer(_ => Expire(attempt.Handle), null, Lifetime, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The attempt, still in the store, for the callback; <see langword="null"/> once it is gone.</summary>
    public Attempt? Find(string handle) => attempts.TryGetValue(handle, out var attempt) ? attempt : null;

    /// <summary>
    /// The callback's result. A token that arrives for an attempt already removed (it expired, or was
    /// taken, during the exchange) is revoked at once.
    /// </summary>
    public void Complete(Attempt attempt, Completion completion)
    {
        bool orphaned;
        lock (attempt)
        {
            attempt.Result = completion;
            orphaned = attempt.Removed;
        }

        if (orphaned && completion.AccessToken is { } token)
            StartRevocation(attempt.ClientName, token);
    }

    /// <summary>Removes the attempt and returns it with its result, or <see langword="null"/> when it is gone.</summary>
    public Attempt? Take(string handle)
    {
        if (!attempts.TryRemove(handle, out var attempt))
            return null;

        lock (attempt)
            attempt.Removed = true;
        attempt.Timer?.Dispose();
        return attempt;
    }

    /// <summary>
    /// Revokes <paramref name="accessToken"/> through the client's <see cref="SparkOneShotOAuthClient.Revoke"/>.
    /// Never throws: a failed revocation is logged as an error, because the token stays valid at the
    /// provider until someone revokes it there.
    /// </summary>
    public async Task RevokeAsync(string clientName, string accessToken, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Clients.TryGetValue(clientName, out var client) || client.Revoke is null)
            return;

        try
        {
            using var http = httpClients.CreateClient(HttpClientName);
            await client.Revoke(new SparkOneShotOAuthRevocation(http, client, accessToken), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "A one-shot OAuth token of client {Client} could not be revoked; it stays valid at the provider until it is revoked there.",
                clientName);
        }
    }

    /// <summary>Revokes in the background (timer callbacks and orphaned tokens have nobody to await them).</summary>
    public void StartRevocation(string clientName, string accessToken)
    {
        var task = RevokeAsync(clientName, accessToken);
        revocations.TryAdd(task, 0);
        _ = task.ContinueWith(t => revocations.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>Completes when every background revocation started so far has finished. For tests.</summary>
    internal Task Idle() => Task.WhenAll(revocations.Keys);

    private void Expire(string handle)
    {
        var attempt = Take(handle);
        if (attempt?.Result?.AccessToken is { } token)
            StartRevocation(attempt.ClientName, token);
    }

    public void Dispose()
    {
        foreach (var attempt in attempts.Values)
            attempt.Timer?.Dispose();
    }

    /// <summary>One authorization attempt.</summary>
    internal sealed class Attempt
    {
        public required string Handle { get; init; }
        public required string UserId { get; init; }
        public required string ClientName { get; init; }
        public required string CodeVerifier { get; init; }
        public required string RedirectUri { get; init; }
        public required string Nonce { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }

        /// <summary>Set once the callback has run. Read and written under the attempt's lock.</summary>
        public Completion? Result { get; set; }

        /// <summary>Whether the attempt has left the store. Read and written under the attempt's lock.</summary>
        public bool Removed { get; set; }

        internal ITimer? Timer { get; set; }

        private int callbackClaimed;

        /// <summary>True for the first callback only: a replayed redirect never exchanges a second code.</summary>
        public bool TryClaimCallback() => Interlocked.Exchange(ref callbackClaimed, 1) == 0;
    }

    /// <summary>What the callback produced: a token, or an error from <see cref="SparkOneShotOAuthErrors"/>.</summary>
    internal sealed record Completion(string? AccessToken, string? GrantedScope, string? Error);
}
