# PRD: GitHubInstallationService — Token Caching

| | |
|---|---|
| **Version** | 2.0 |
| **Date** | 2026-04-13 |
| **Status** | Proposed |
| **Owner** | MintPlayer |
| **Package** | `MintPlayer.Spark.Webhooks.GitHub` |

> v2 supersedes v1 after reviewing the working pattern in `C:\Repos\ProjectDashboard\ProjectDashboard.Library\GitClient.cs`. v1 proposed caching the App client (JWT, 10-min TTL). v2 caches **installation access tokens** (1-hour TTL) — a much bigger win that also eliminates the per-call HTTP roundtrip to mint a token.

---

## 1. Problem Statement

`GitHubInstallationService` re-mints everything on every call:

- `CreateAppClientAsync()` reads the PEM, signs a fresh JWT, allocates a new `GitHubClient` — every call.
- `CreateInstallationClientAsync(installationId)` calls `CreateAppClientAsync()` (fresh JWT) **plus** an HTTP roundtrip to `POST /app/installations/{id}/access_tokens`, then allocates another `GitHubClient` — every call.
- `CreateGraphQLConnectionAsync(installationId, …)` calls one of the above — every call.

For a single webhook, this can fire 3+ times in milliseconds. The wasted JWT signing was tolerable; the wasted **HTTP roundtrip per installation token** is not — it's the dominant cost and the most likely source of cascading 401/404 errors when calls overlap.

A working reference for the right pattern already exists in a sibling repo:

**`C:\Repos\ProjectDashboard\ProjectDashboard.Library\GitClient.cs`** — caches the installation access token in a singleton, guards the refresh path with `SemaphoreSlim`, recreates only when the cached token is within 1 minute of `ExpiresAt`. The App client is *not* cached; it's created fresh during each (rare) token refresh.

---

## 2. Goals

1. **Cache installation access tokens** keyed by `installationId`, with TTL governed by GitHub's response (`AccessToken.ExpiresAt`, ~1 hour).
2. **Refresh transparently** when the cached token has < 60 seconds remaining.
3. **Async-safe concurrency** via `SemaphoreSlim` — multiple webhook handlers and HTTP requests must be able to read concurrently while serializing the rare refresh.
4. **Don't cache the App client** — the JWT-bearing client is only needed during a token refresh (once per hour per installation). Match the ProjectDashboard pattern.
5. **No public API change** — `IGitHubInstallationService` signatures stay the same.

### Non-goals

- Caching App clients (JWT). Re-creating one per refresh is cheap relative to the refresh frequency.
- Pre-warming tokens at startup.
- Distributed cache (single-process is sufficient for now).
- Caching across process restarts.

---

## 3. Design

### 3.1 Service lifetime

**Current**: `[Register(typeof(IGitHubInstallationService), ServiceLifetime.Scoped)]` at `MintPlayer.Spark.Webhooks.GitHub/Services/GitHubInstallationService.cs:13`.

**New**: `ServiceLifetime.Singleton`.

The service is stateless from the caller's perspective. The only injected dependency is `IOptions<GitHubWebhooksOptions>`, which is singleton-safe. Promoting to singleton is a prerequisite for any cross-call caching.

### 3.2 Cached state

```csharp
private readonly ConcurrentDictionary<long, AccessToken> _installationTokens = new();
private readonly SemaphoreSlim _refreshGate = new(initialCount: 1, maxCount: 1);
```

**Why a dictionary keyed by `installationId`**: Spark webhooks may serve multiple GitHub App installations (different orgs, different users). The ProjectDashboard reference targets exactly one organization and uses a single `AccessToken?` field — we generalize to a dictionary.

**Why a single `SemaphoreSlim` instead of one per installation**: Refresh contention is near-zero (once per hour per installation). A single global gate is simpler and avoids dictionary-of-semaphores cleanup. If profiling later shows contention, switch to per-installation semaphores.

### 3.3 Refresh logic

Add a private helper that all token-consuming methods funnel through:

```csharp
private async Task<AccessToken> GetOrCreateInstallationTokenAsync(long installationId, CancellationToken ct)
{
    // Fast path — no semaphore needed for a still-fresh token
    if (_installationTokens.TryGetValue(installationId, out var cached)
        && DateTimeOffset.UtcNow.AddSeconds(60) < cached.ExpiresAt)
    {
        return cached;
    }

    await _refreshGate.WaitAsync(ct);
    try
    {
        // Re-check inside the gate (another caller may have refreshed)
        if (_installationTokens.TryGetValue(installationId, out cached)
            && DateTimeOffset.UtcNow.AddSeconds(60) < cached.ExpiresAt)
        {
            return cached;
        }

        var appClient = await CreateAppClientAsync();
        var fresh = await appClient.GitHubApps.CreateInstallationToken(installationId);
        _installationTokens[installationId] = fresh;
        return fresh;
    }
    finally
    {
        _refreshGate.Release();
    }
}
```

**Why `SemaphoreSlim` over `lock`**: The refresh path `await`s an HTTP call. `lock` cannot be held across `await`. `SemaphoreSlim.WaitAsync` is the canonical async-aware primitive.

`ConcurrentDictionary` carries the per-key reads/writes; the semaphore guards the "don't mint two tokens for the same installation in parallel" invariant. Even if two callers race past the semaphore for *different* installations, that's fine — they'd serialize anyway because the gate is global, but only one HTTP roundtrip per installation per refresh window will occur.

### 3.4 Per-installation client cache + dynamic credential store

The token cache from §3.3 covers the small DTO; the heavier `IGitHubClient` and `Octokit.GraphQL.Connection` instances they back also need to be cached and disposed correctly. Naïvely caching a client with `InMemoryCredentialStore(new Credentials(token.Token))` bakes the token into the client at construction — when the token refreshes, the client becomes "stale" and we'd have to dispose it and rebuild.

We avoid that entirely by using a **dynamic credential store** that calls back into `_installationTokens` on every request. The same `IGitHubClient` instance keeps working across token refreshes — the next request just sees the new token. Mid-life disposal becomes unnecessary.

#### 3.4.1 Two new caches

```csharp
private readonly ConcurrentDictionary<long, IGitHubClient> _installationClients = new();
private readonly ConcurrentDictionary<long, GraphQLConnection> _installationGraphQLConnections = new();
private readonly List<IDisposable> _ownedDisposables = new();  // populated under _refreshGate during cache misses
```

Both caches are populated lazily on first `CreateInstallationClientAsync` / `CreateGraphQLConnectionAsync(_, Installation)` for a given `installationId`, then reused for that process's lifetime. Disposal happens in `IDisposable.Dispose()` at service shutdown — see §3.4.4.

#### 3.4.2 Dynamic credential store — REST

```csharp
internal sealed class DynamicInstallationCredentialStore : ICredentialStore
{
    private readonly long _installationId;
    private readonly GitHubInstallationService _service;

    public DynamicInstallationCredentialStore(long installationId, GitHubInstallationService service)
    {
        _installationId = installationId;
        _service = service;
    }

    public async Task<Credentials> GetCredentials()
    {
        var token = await _service.GetOrCreateInstallationTokenAsync(_installationId, CancellationToken.None);
        return new Credentials(token.Token);
    }
}
```

Octokit's `Connection` calls `credentialStore.GetCredentials()` on every outbound request, so the cached client always uses the freshest cached token without needing to be rebuilt.

#### 3.4.3 Dynamic credential store — GraphQL

```csharp
internal sealed class DynamicInstallationGraphQLCredentialStore : Octokit.GraphQL.ICredentialStore
{
    private readonly long _installationId;
    private readonly GitHubInstallationService _service;

    public DynamicInstallationGraphQLCredentialStore(long installationId, GitHubInstallationService service)
    {
        _installationId = installationId;
        _service = service;
    }

    public async Task<string> GetCredentials(CancellationToken cancellationToken)
    {
        var token = await _service.GetOrCreateInstallationTokenAsync(_installationId, cancellationToken);
        return token.Token;
    }
}
```

#### 3.4.4 Disposal

`GitHubInstallationService` implements `IDisposable`. On `Dispose()`:

1. Dispose `_refreshGate` (semaphore).
2. For each `IDisposable` recorded in `_ownedDisposables` (the per-installation `HttpClient` instances created for GraphQL connections, and the shared REST `HttpClientAdapter`), call `Dispose()`.
3. Clear `_installationClients` and `_installationGraphQLConnections`.

The cached `IGitHubClient` instances themselves are not `IDisposable` in Octokit; only the underlying `HttpClientAdapter` / `HttpClient` they reference are. We track those by reference in `_ownedDisposables` at construction time.

### 3.5 Public API changes

```csharp
public Task<IGitHubClient> CreateInstallationClientAsync(long installationId)
{
    return Task.FromResult(_installationClients.GetOrAdd(installationId, BuildInstallationClient));
}

private IGitHubClient BuildInstallationClient(long installationId)
{
    var refreshing = new TokenRefreshingHttpClient(_sharedRestHttpClient, installationId, this);
    var connection = new Connection(
        new ProductHeaderValue("SparkWebhooks", "1.0"),
        GitHubClient.GitHubApiUrl,
        new DynamicInstallationCredentialStore(installationId, this),
        refreshing,
        new SimpleJsonSerializer());
    return new GitHubClient(connection);
}

public async Task<GraphQLConnection> CreateGraphQLConnectionAsync(long installationId, EClientType clientType)
{
    switch (clientType)
    {
        case EClientType.App:
            // App-mode GraphQL is rare and never cached — mint a fresh JWT-bearing connection per call.
            var appClient = await CreateAppClientAsync();
            var appToken = appClient.Connection.Credentials.Password;
            return new GraphQLConnection(
                new GraphQLProductHeaderValue("SparkWebhooks", "1.0"),
                appToken);
        case EClientType.Installation:
            return _installationGraphQLConnections.GetOrAdd(installationId, BuildInstallationGraphQLConnection);
        default:
            throw new ArgumentOutOfRangeException(nameof(clientType), clientType, null);
    }
}

private GraphQLConnection BuildInstallationGraphQLConnection(long installationId)
{
    var handler = new TokenRefreshingHandler(installationId, this) { InnerHandler = new HttpClientHandler() };
    var httpClient = new HttpClient(handler);
    lock (_ownedDisposables) { _ownedDisposables.Add(httpClient); }

    return new GraphQLConnection(
        new GraphQLProductHeaderValue("SparkWebhooks", "1.0"),
        GraphQLConnection.GithubApiUri,
        new DynamicInstallationGraphQLCredentialStore(installationId, this),
        httpClient);
}
```

Notes:
- `_sharedRestHttpClient` is one `HttpClientAdapter` allocated as a service field and added to `_ownedDisposables` at construction.
- The per-installation GraphQL `HttpClient` is added to `_ownedDisposables` at cache-miss time so we can dispose it on shutdown. The lock around that list is contended only on cache misses — once per installation per process.
- `GetOrAdd` on `ConcurrentDictionary` may invoke the factory more than once under racing cache misses, but only one of the values is published; the other is GC'd. Acceptable because construction is cheap and idempotent.

The `CreateAppClientAsync` method (used by the App branch of `CreateGraphQLConnectionAsync` and by the token-refresh path inside `GetOrCreateInstallationTokenAsync`) is **not** cached — App clients are short-lived JWT-bearing instances used only during the rare token-refresh path:

```csharp
public async Task<IGitHubClient> CreateAppClientAsync()
{
    var opts = _options.Value;
    var privateKey = await ResolvePrivateKeyAsync(opts);
    var jwt = CreateJwt(opts.ClientId!, privateKey);

    return new GitHubClient(new ProductHeaderValue("SparkWebhooks", "1.0"))
    {
        Credentials = new Credentials(jwt, AuthenticationType.Bearer),
    };
}
```

After this design:
- **Token cache** (§3.3): one `AccessToken` per installation, refreshed on natural expiry.
- **Client cache** (§3.4): one `IGitHubClient` + one `GraphQLConnection` per installation, lifetime = service. Same instance keeps working across token refreshes via the dynamic credential stores.
- **Per-call allocations**: zero for cached paths; only the App branch and the rare cache-miss path allocate.

### 3.6 Transparent 401 retry (token refresh on stale cache)

Cached installation tokens can become stale before their nominal `ExpiresAt` — most commonly due to host clock drift / suspend-resume. A stale token surfaces as HTTP 401 from GitHub on the next API call. Without retry, the caller sees the exception and the cache continues serving the dead token until natural TTL.

**Goal**: the retry happens **inside the library**, transparently to caller code in `LogIssues`, `GitHubProjectService`, `GitHubProjectsController`, and any future caller. No opt-in, no wrapper API, no caller changes.

**Mechanism**: when `GitHubInstallationService` constructs an `IGitHubClient` or `Octokit.GraphQL.Connection` for a specific `installationId`, it injects an HTTP-layer interceptor bound to that `installationId`. On a 401 response, the interceptor:

1. Calls `InvalidateInstallation(installationId)` on the service to remove the stale token.
2. Awaits `GetOrCreateInstallationTokenAsync(installationId, ...)` to get a freshly minted token.
3. Rewrites the `Authorization` header on the original request.
4. Re-sends once. If that also returns 401, the response propagates to the caller (genuine auth failure — not stale-token).

#### 3.6.1 REST interceptor — `Octokit.Internal.IHttpClient`

```csharp
internal sealed class TokenRefreshingHttpClient : IHttpClient
{
    private readonly IHttpClient _inner;
    private readonly long _installationId;
    private readonly GitHubInstallationService _service;  // internal back-reference

    public TokenRefreshingHttpClient(IHttpClient inner, long installationId, GitHubInstallationService service)
    {
        _inner = inner;
        _installationId = installationId;
        _service = service;
    }

    public async Task<IResponse> Send(IRequest request, CancellationToken cancellationToken)
    {
        var response = await _inner.Send(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        _service.InvalidateInstallation(_installationId);
        var fresh = await _service.GetOrCreateInstallationTokenAsync(_installationId, cancellationToken);
        request.Headers["Authorization"] = $"token {fresh.Token}";
        return await _inner.Send(request, cancellationToken);
    }

    public void SetRequestTimeout(TimeSpan timeout) => _inner.SetRequestTimeout(timeout);
    public void Dispose() => _inner.Dispose();
}
```

The `TokenRefreshingHttpClient` is wired by `BuildInstallationClient` (see §3.5) wrapping the **shared** `_sharedRestHttpClient`. No per-call disposable allocation occurs.

#### 3.6.2 GraphQL interceptor — `DelegatingHandler`

`Octokit.GraphQL.Connection` accepts a .NET `HttpClient`, so we use a standard `DelegatingHandler`:

```csharp
internal sealed class TokenRefreshingHandler : DelegatingHandler
{
    private readonly long _installationId;
    private readonly GitHubInstallationService _service;

    public TokenRefreshingHandler(long installationId, GitHubInstallationService service)
    {
        _installationId = installationId;
        _service = service;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        _service.InvalidateInstallation(_installationId);
        var fresh = await _service.GetOrCreateInstallationTokenAsync(_installationId, ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh.Token);
        return await base.SendAsync(request, ct);
    }
}
```

The `TokenRefreshingHandler` is wired by `BuildInstallationGraphQLConnection` (see §3.5) into a **cached** per-installation `HttpClient`. The App-mode branch does **not** wire the interceptor — JWT-bearing connections aren't cached and there's no token to invalidate.

#### 3.6.3 New private API on `GitHubInstallationService`

```csharp
internal void InvalidateInstallation(long installationId)
    => _installationTokens.TryRemove(installationId, out _);
```

Marked `internal` so only the interceptor classes (in the same assembly) can call it. Not part of `IGitHubInstallationService`.

`GetOrCreateInstallationTokenAsync` is also marked `internal` so the interceptors can re-mint after invalidation.

#### 3.6.4 Retry budget

**Exactly one retry per failed request.** If the second attempt also returns 401, we propagate. This bounds worst-case latency at 2× normal and prevents retry storms if credentials are genuinely revoked.

#### 3.6.5 Loop-invariance of the refresh

Because `InvalidateInstallation` is called *before* `GetOrCreateInstallationTokenAsync`, the second call always goes through the slow refresh path under the semaphore — no chance of returning the same stale token from the cache. If two concurrent requests both hit 401 for the same installation, the semaphore in `GetOrCreateInstallationTokenAsync` ensures only one HTTP roundtrip is made to mint the new token; both interceptors then receive the same fresh token.

The dynamic credential stores (§3.4.2 and §3.4.3) ensure that any **subsequent** request through the cached client also picks up the fresh token automatically — the 401 retry mechanism is a fallback for in-flight requests that crossed the staleness boundary.

### 3.7 JWT lifetime fix

The current `CreateJwt` uses `iat = now - 1s`, `exp = now + 9min`. Two fixes per [GitHub's documentation](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-jwt-for-a-github-app) and the ProjectDashboard pattern:

- `iat = now - 60s` — accounts for clock skew between Spark host and GitHub.
- `exp = iat + 10min` — use the full max GitHub allows (matches ProjectDashboard).

JWT is now minted ~once per installation per hour (only during token refresh), so the change has no measurable cost.

### 3.8 Concurrency / mutation safety

- `_installationTokens` (`ConcurrentDictionary<long, AccessToken>`) — atomic read/write per key.
- `_installationClients` and `_installationGraphQLConnections` (`ConcurrentDictionary<long, …>`) — same. `GetOrAdd`'s factory may race; one value wins, the other is GC'd.
- `_refreshGate` (`SemaphoreSlim`) — serializes the rare token-refresh path.
- `_ownedDisposables` (`List<IDisposable>`) — guarded by a per-list `lock` on append. Read only on `Dispose()` (single-threaded by definition).
- Cached `IGitHubClient` instances **are** shared across callers. This is safe because Octokit documents `GitHubClient`/`Connection` as thread-safe for reads-after-construction, and we never mutate `Credentials` post-construction (the dynamic credential store handles all credential changes).
- Cached `AccessToken` is an immutable Octokit DTO. Concurrent reads are safe.

### 3.9 Async-all-the-way

`ResolvePrivateKey` currently uses `File.ReadAllText` synchronously. Rename it to `ResolvePrivateKeyAsync` and switch to `File.ReadAllTextAsync`:

```csharp
private static async Task<string> ResolvePrivateKeyAsync(GitHubWebhooksOptions opts)
{
    var privateKey = opts.PrivateKeyPem;
    if (string.IsNullOrEmpty(privateKey))
    {
        if (string.IsNullOrEmpty(opts.PrivateKeyPath))
            throw new InvalidOperationException(
                "GitHub App authentication requires either PrivateKeyPem or PrivateKeyPath to be configured.");

        var absolutePath = Path.IsPathRooted(opts.PrivateKeyPath)
            ? opts.PrivateKeyPath
            : Path.Combine(Directory.GetCurrentDirectory(), opts.PrivateKeyPath);
        privateKey = await File.ReadAllTextAsync(absolutePath);
    }

    if (string.IsNullOrEmpty(opts.ClientId))
        throw new InvalidOperationException(
            "GitHub App authentication requires ClientId to be configured.");

    return privateKey;
}
```

`CreateJwt` stays synchronous — RSA signing is pure CPU and doesn't benefit from async.

### 3.10 What does NOT change

- `IGitHubInstallationService` interface — unchanged.
- All callers (`LogIssues`, `GitHubProjectService`, `GitHubProjectsController`) — no changes required.
- Installation token TTL — governed by GitHub (1 hour); we just respect what GitHub returns.

---

## 4. Implementation Plan

### Phase 1 — Refactor

1. Change `[Register(...)]` lifetime to `Singleton`. Implement `IDisposable` to dispose `_refreshGate`, `_sharedRestHttpClient`, and all entries in `_ownedDisposables`.
2. Add caches: `_installationTokens`, `_installationClients`, `_installationGraphQLConnections` (all `ConcurrentDictionary`), `_refreshGate` (`SemaphoreSlim`), `_sharedRestHttpClient` (single shared `HttpClientAdapter`), `_ownedDisposables` (`List<IDisposable>`).
3. Add `GetOrCreateInstallationTokenAsync` and `InvalidateInstallation` as `internal` members.
4. Add credential store classes: `DynamicInstallationCredentialStore` (REST), `DynamicInstallationGraphQLCredentialStore` (GraphQL).
5. Add interceptor classes: `TokenRefreshingHttpClient : Octokit.Internal.IHttpClient` (REST), `TokenRefreshingHandler : DelegatingHandler` (GraphQL).
6. Refactor `CreateInstallationClientAsync` to look up `_installationClients`, populating via `BuildInstallationClient` (uses shared HTTP layer + dynamic credential store).
7. Refactor `CreateGraphQLConnectionAsync` (Installation branch) to look up `_installationGraphQLConnections`, populating via `BuildInstallationGraphQLConnection`. App branch keeps the per-call mint pattern.
8. Rewrite `CreateAppClientAsync` as a true `async` method (no `Task.FromResult`) that calls `ResolvePrivateKeyAsync` and `CreateJwt`.
9. Rename `ResolvePrivateKey` → `ResolvePrivateKeyAsync` and switch `File.ReadAllText` → `File.ReadAllTextAsync`.
10. Update `CreateJwt` to use `iat = now - 60s`, `exp = iat + 10min`.

### Phase 2 — Verification

- Manual: trigger a webhook 3× within an hour; observe via debugger or logging that `CreateInstallationToken` only runs the first time, and the cached token is reused for the next ~59 minutes.
- Manual: wait > 1 hour, trigger again; observe a single refresh.
- Optional unit test: 100 concurrent calls to `CreateInstallationClientAsync` for the same `installationId` produce exactly one HTTP call (mock the App client).

### Phase 3 — Out of scope (future PRDs)

- Per-installation semaphores (only if global gate causes measurable contention).
- Cache invalidation hook for when an installation is uninstalled (currently the stale entry just gets refreshed and 404s once, then the caller decides what to do).
- Pre-warming via `IHostedService`.

---

## 5. Risks + Open Questions

| Risk | Mitigation |
|---|---|
| Singleton lifetime exposes a thread-safety bug we didn't anticipate. | `ConcurrentDictionary` + `SemaphoreSlim` are standard. Octokit clients are not shared. Low risk; covered by code review. |
| `IOptions<GitHubWebhooksOptions>` reload — if `ClientId` or `PrivateKeyPem` changes at runtime, cached tokens become orphaned (still valid but minted for old config). | **Not planned.** Credential values are not expected to change at runtime; rotation requires a process restart. The `[Options]` source generator (`MintPlayer.Dotnet.Tools.SourceGenerators`) does not currently support `IOptionsMonitor<T>`. If hot-rotation ever becomes a requirement: extend the generator, switch to `IOptionsMonitor<T>`, and clear `_installationTokens` from an `OnChange` handler. |
| Clock skew on the Spark host (backward jump) makes a token outlive its real validity. | **Covered by §3.5 transparent retry.** First call after staleness gets 401 → interceptor invalidates + refreshes + retries. Caller sees no failure; cost is one extra HTTP roundtrip. |
| Clock skew on the Spark host (forward jump) makes a token "expire" early in our calculation. | Cost is one early refresh per affected installation — negligible. The 60s safety buffer plus GitHub's own ~60s tolerance covers normal drift. |
| Memory growth if many distinct installations are seen over time. | `AccessToken` cache: ~200–500 bytes per entry (~5 MB at 10k installations). Cached `IGitHubClient` + `GraphQLConnection` per installation: each holds an `HttpClient`/`HttpClientAdapter` (~10–50 KB depending on connection-pool state). At 10k installations actively making both REST and GraphQL calls: ~1 GB ceiling. **Not currently planned**: LRU/TTL eviction. Add only if profiling shows it matters. |

---

## 6. Acceptance Criteria

- [ ] `GitHubInstallationService` registered as `Singleton`.
- [ ] First call to `CreateInstallationClientAsync(id)` performs one HTTP roundtrip; subsequent calls within 59 minutes perform zero.
- [ ] Calls for **different** installation IDs each get their own cached token; no cross-talk.
- [ ] 100 concurrent calls for the same `installationId` produce exactly **one** HTTP roundtrip.
- [ ] After cached token is within 60s of `ExpiresAt`, the next call triggers exactly one refresh; concurrent callers during refresh wait and return the new token.
- [ ] Existing webhook flows (`LogIssues`, `MoveItemOnProjectBoard`, `GitHubProjectsController.ListProjects`) continue to work unchanged.
- [ ] No public API changes to `IGitHubInstallationService`.
- [ ] JWT `iat = now - 60s`, `exp = iat + 10min`.
- [ ] A 401 response from a REST API call made via a client returned by `CreateInstallationClientAsync` triggers exactly one transparent retry with a freshly minted token. Caller sees success.
- [ ] Same for a GraphQL call via `CreateGraphQLConnectionAsync(_, EClientType.Installation)`.
- [ ] If the second attempt also returns 401, the exception propagates to the caller (no retry storm).
- [ ] Concurrent 401s for the same installation result in exactly one token refresh (semaphore in `GetOrCreateInstallationTokenAsync`).
- [ ] Two consecutive `CreateInstallationClientAsync(id)` calls for the same `id` return the **same** `IGitHubClient` reference. Same for `CreateGraphQLConnectionAsync(id, EClientType.Installation)`.
- [ ] After a token refresh, the existing cached `IGitHubClient` instance continues to work — the next request through it uses the new token (verified by the dynamic credential store).
- [ ] On `GitHubInstallationService.Dispose()`, all cached `HttpClient`/`HttpClientAdapter` instances are disposed exactly once, no exceptions.

---

## 7. References

- **Working reference implementation**: `C:\Repos\ProjectDashboard\ProjectDashboard.Library\GitClient.cs` — `GetOrCreateInstallationToken` (lines 86–106), `NewAppClient` (lines 68–84), `NewInstallationClient` (lines 111–116), `NewGraphQLClient` (lines 121–125).
- [GitHub: Generating a JWT for a GitHub App](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-jwt-for-a-github-app)
- [GitHub: Authenticating as a GitHub App installation](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation)
- Source: `MintPlayer.Spark.Webhooks.GitHub/Services/GitHubInstallationService.cs`
- Callers: `LogIssues.cs`, `GitHubProjectService.cs`, `GitHubProjectsController.cs`

---

## 8. Differences from ProjectDashboard reference

| Aspect | ProjectDashboard | This PRD |
|---|---|---|
| Cache shape | Single `AccessToken?` field | `ConcurrentDictionary<long, AccessToken>` |
| Number of installations supported | One (configured org) | N (multi-tenant) |
| Refresh primitive | `SemaphoreSlim` (1, 1) | Same |
| App client caching | None | None |
| Installation `IGitHubClient` caching | Per-call construction | **Cached per installation** with dynamic credential store (§3.4) |
| Installation `GraphQLConnection` caching | Per-call construction | **Cached per installation** with dynamic credential store (§3.4) |
| HTTP-layer disposal | n/a | Service `IDisposable` disposes shared REST adapter + per-installation GraphQL `HttpClient`s |
| JWT `iat`/`exp` | `-60s` / `+10min` | Same |
| Token refresh trigger | < 1 min remaining | Same |
| 401 retry | None — caller sees the exception | **Transparent retry** at REST + GraphQL HTTP layer (§3.5) |
| Disposal | `IDisposable` to dispose semaphore | Same (will add `IDisposable`) |
