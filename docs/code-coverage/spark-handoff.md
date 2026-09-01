# Handoff: MintPlayer.Spark work for the Coverage project (M0)

> **Status (2026-08-10): RESOLVED** by [MintPlayer.Spark#231](https://github.com/MintPlayer/MintPlayer.Spark/pull/231)
> (merged 2026-08-09, published as `10.0.0-preview.42` + `@mintplayer/ng-spark-auth@22.1.0`).
> Items 1, 3, 4, 5, 6 fixed; item 2 (ApiTokens library) **deliberately cancelled** in favour of
> OAuth2 `client_credentials` via the new `MintPlayer.Spark.IdentityProvider` — Coverage keeps its
> app-local `covt_` tokens (see PR docs `PRD-CoverageHandoff.md`). Upgrade checklist for this repo
> lives in the Spark repo's `docs/release-notes-preview-42.md`. Kept for historical context.

## NEW (2026-08-13): bug — a crash mid-handler strands the message in `Processing` forever

Found while diagnosing Coverage's parse-session outage (the outage itself was a
Coverage bug — request-budget exhaustion — but this adjacent hazard is Spark's).

`MessageSubscriptionWorker.ProcessBatchAsync` sets `Status = Processing` and
**persists it before the handlers have run** (first pickup saves at the
"populate handler list" step, `MessageSubscriptionWorker.cs:141`; later saves
happen between handlers). The subscription that feeds the worker only matches
`Status = 'Pending'` (`MessageSubscriptionWorker.cs:56`), and RavenDB
subscriptions only re-send a document when it *changes*. So a process that
dies between "persisted Processing" and "persisted the terminal status" —
a deploy during a slow handler is the realistic case — leaves the message in
`Processing`, where **nothing will ever match it again**: no lease expiry, no
visibility timeout, no reaper.

Suggested fix (either works):
- a startup/periodic reaper that flips `Processing` messages older than a
  lease window back to `Pending` (attempt count already exists to cap redelivery), or
- include `Processing` with a `PickedUpAtUtc < now - lease` predicate in the
  subscription query itself.

Coverage's exposure: every deploy tears down the container while
`coverage-parse-session` may be mid-parse; the stranded message's session then
never parses (its build shows "Pending" until the finalize-timeout marks it
Failed). Successors are NOT blocked — the stuck doc simply stops matching the
subscription, so the queue silently skips it, which also makes the loss easy
to miss. (Verified against `MintPlayer.Spark` @ preview.42 sources; not yet
reproduced live.)

## NEW (2026-08-12): bug — Smee dev tunnel's re-minification breaks installation-event signatures

Filed as [MintPlayer.Spark#232](https://github.com/MintPlayer/MintPlayer.Spark/issues/232)
(full analysis incl. why System.Text.Json only half-helps: JsonDocument fixes the scalar
reinterpretation but Utf8JsonWriter re-escapes strings — only lexical whitespace-stripping
is byte-exact by construction).

For a Spark session in `C:\Repos\MintPlayer.Spark`. `SmeeBackgroundService`
(libs/webhooks/MintPlayer.Spark.Webhooks.GitHub.DevTunnel/Services/SmeeBackgroundService.cs:49-51)
re-minifies the smee-relayed body — correctly, since GitHub signs minified bytes and smee
pretty-prints — but does it with `JsonConvert.SerializeObject(JsonConvert.DeserializeObject(...))`,
which *reinterprets scalars* instead of just stripping whitespace:

- default `DateParseHandling` rewrites fractional-second timestamps:
  `"2026-08-12T08:45:12.000+02:00"` → `"2026-08-12T08:45:12+02:00"` (measured);
- float parsing rewrites trailing zeros: `1.50` → `1.5` (measured).

GitHub's **installation / installation_repositories** events carry exactly those
`.000`-style timestamps, so their HMAC never matches and `SparkWebhookEventProcessor`
drops them ("signature validation failed") — with a correct webhook secret. Push events
happen to survive (no fractional timestamps), which hides the bug until someone installs
an App through the tunnel. Coverage hit this live on its first real installation.

**Fix**: minify *lexically* — remove whitespace outside string literals, copy every token
verbatim (JS pretty-printing only ever adds whitespace, so this reconstructs the signed
bytes exactly). Working implementation + unit tests to lift: Coverage repo,
`Coverage/Services/SmeeWebhookTunnelService.cs` (`LexicalMinify`) and
`Coverage.Tests/Services/SmeeWebhookTunnelServiceTests.cs`. Note `e.Data.Body` from
Smee.IO.Client may already be a parsed JToken (its `ToString()` pretty-prints and its
internal Newtonsoft parse may already have converted date strings) — if so, the fix also
needs the raw SSE frame rather than the parsed Dto, as in Coverage's replacement service.
Once fixed upstream, Coverage swaps back to `options.AddSmeeDevTunnel(...)` and deletes
its local tunnel.

## NEW (2026-08-12): docs-only item — App walkthrough misses the sign-in email permission

For a Spark session in `C:\Repos\MintPlayer.Spark`. Found while doing Coverage's first real
GitHub sign-in: the App-creation walkthrough
(`libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/README.md`, permissions tables around line 47)
documents repository/organization permissions but not the **account** permission that Spark's own
external login depends on. Any app whose GitHub App is also the sign-in provider (via
`identity.AddGitHub(...)`) needs **Account permissions → Email addresses: Read-only**:
`GitHubAuthenticationExtensions.OnCreatingTicket` calls `GET /user/emails` to attest a verified
primary email, and the external-login callback (R2-H11) refuses to auto-provision a first-time
user without that attestation — the OAuth popup completes, then sign-in fails `email_not_verified`
with nothing in the server logs. Suggested addition after the organization-permissions note:

- An "Account permissions (only if the app is also used for sign-in via Spark's `AddGitHub`)"
  table with **Email addresses / Read-only** and the failure symptom above.
- A note that adding an account permission does **not** upgrade previously authorized user
  tokens — users get an "additional permissions" prompt on the next authorize, or must revoke
  the old authorization (GitHub → Settings → Applications → Authorized GitHub Apps).
- While there: §537's note (scopes are ignored for GitHub Apps) is the reason this must be an
  App permission rather than a `user:email` scope — worth cross-linking the two notes.

Work items for a Claude session running in `C:\Repos\MintPlayer.Spark`. Discovered
during the Coverage-analyzer investigation (see `docs/PRD.md` in MintPlayer/CodeCoverage,
§10 and PLAN.md M0). Branch from `master` (note: local checkout sits on `security-audit`,
one docs-only commit ahead — confirm intended base). One PR for the lot.

## NEW (2026-08-18): enhancement — rate-limiter gaps + a `SparkTestDriver` licence option

> **✅ RESOLVED** — filed as [MintPlayer.Spark#265](https://github.com/MintPlayer/MintPlayer.Spark/issues/265),
> fixed by [#266](https://github.com/MintPlayer/MintPlayer.Spark/pull/266), shipped in
> `10.0.0-preview.52`. Both rate-limiter items below are addressed, plus a third: `SparkTestDriver`
> now tolerates an absent licence (org secrets are not exposed to fork pull requests) while still
> failing loudly on an *invalid* one — Coverage's local equivalent is
> `Coverage.Tests/CoverageRavenTest.cs`.
>
> Coverage has adopted it: `spark.AddRateLimiter(o => o.PathPrefixes = [...])` in `Program.cs`, with
> the hand-rolled `GlobalLimiter` and the manual `app.UseRateLimiter()` deleted. Kept below as the
> record of what was wrong and why, since the reasoning outlived the workaround.
>
> One finding from the review is worth carrying: the fix's first routing guard was a **false
> positive** for minimal-hosting apps, which never call `UseRouting()` explicitly — `WebApplication`
> stamps `__GlobalEndpointRouteBuilder`, not the `__EndpointRouteBuilder` the guard checked. Fixed
> upstream before merge. This app is unaffected either way: it calls `app.UseRouting()` explicitly.

`spark.AddRateLimiter()` is a good primitive that Coverage ended up **not** using, for two
reasons that are both fixable upstream and neither of which is a criticism of the design.

**1. The path scoping is hardcoded.** `SparkBuilderRateLimiterExtensions.cs:52` tests
`/spark` and `/connect` literally, and `SparkRateLimiterOptions` exposes only `PermitLimit`
and `Window`. An app that also wants its own anonymous read surface metered — Coverage has
`/api/browse`, serving the same documents as `/spark` — cannot say so, and ends up
hand-rolling a global limiter that duplicates the extension's body.

*Suggested shape*: `PathPrefixes` on `SparkRateLimiterOptions`, defaulting to
`["/spark", "/connect"]` so nothing changes for existing callers.

**2. The middleware lands after authentication, and callers can't move it.** The extension
registers `app.UseRateLimiter()` through the builder registry, and `registry.ApplyMiddleware`
runs at the *end* of `UseSpark` — after `UseAuthentication`. An app whose flood risk is on an
authenticated ingest endpoint wants the limiter to reject before a token lookup happens, and
today the only way to get that is to register `UseRateLimiter()` by hand and skip the
extension entirely (calling both runs `RateLimitingMiddleware` twice, and ASP.NET Core has no
idempotence marker on it, so every request is charged twice against the same partition).

*Suggested shape*: either place the limiter ahead of `UseAuthentication` inside `UseSpark`
(it needs only routing, which has already run), or expose the registration point so an app
can opt into "before auth". A note in the XML doc that the two cannot be combined would be
worth having regardless — it is a silent double-charge, not an error.

Coverage's local workaround: a hand-configured `GlobalLimiter` scoped to `/spark`, plus named
policies for its own endpoints, all under one early `app.UseRateLimiter()`. It is deleted in
favour of configuration the moment (1) and (2) land. Context:
`docs/upload-result-contract.md` §6.1 / N5.

## NEW (2026-08-19): bug — `[GenerateIndex]` maps complex-typed properties, which Corax faults on every document

**FILED — [Spark#273](https://github.com/MintPlayer/MintPlayer.Spark/issues/273)**, open. Hit while
adopting `[GenerateIndex]` here (`preview.53`): the generator maps every model property verbatim
with default indexing, and Corax throws `NotSupportedInCoraxException` on any non-null complex value
— so `Builds_Overview` (`Sessions`, `Coverage`) and `Repositories_Overview` (`LatestCoverage`)
faulted on every document, ending with zero index entries and empty grids. No compile-time signal;
the demos never trip it because the only `[GenerateIndex]` demo entity (`Fleet.Car`) has no complex
field on disk.

The issue carries the full PRD: auto-classify complex properties in `Describe()` and emit
`Index(name, FieldIndexing.No)` while keeping them mapped and stored (dropping them instead would
blank AsDetail columns — `EntityMapper` reads the full object off the projection and silently skips
absent properties), a new `SPARK_INDEX_010` Info diagnostic, and a follow-up that derives a sortable
scalar companion from the complex type's `[Breadcrumb]` template riding the existing
`{Name}Sort`/`[IgnoreProperty]`/`ResolveSortProperty` convention — under a **generated name distinct
from the entity property**, which is structural: `ModelSynchronizer` throws on a same-name type
mismatch between projection and entity. Eight spikes enumerated, including duplicate-`Index()`
semantics (decides how our workaround is retired) and `System.Drawing.Color`'s persistence shape
(decides whether Fleet is latently broken).

**✅ SHIPPED in `10.0.0-preview.55`** (Spark PR #278) and adopted here 2026-08-20: the generator
classifies complex fields by serialized shape and emits `Index(field, FieldIndexing.No)` itself, with
`SPARK_INDEX_010` (Warning, not Info) naming each one — seven fire in this repo. The local workaround
`Coverage/Indexes/GeneratedIndexes.ComplexFields.cs` has been **deleted**; the duplicate-`Index()` spike
resolved as "throws", so keeping it was not an option. The class-level `[Breadcrumb("template")]`
attribute was deleted in the same release (templates live in the model JSON now), which is why the four
entity CLR-shape hashes moved on upgrade. See `docs/adopt-spark-preview-57.md`.

## NEW (2026-08-18): bug — `IndexRegistry` silently rebinds a collection to whichever index registers last

**FILED — [Spark#272](https://github.com/MintPlayer/MintPlayer.Spark/issues/272)**, open. Found while
scoping `[GenerateIndex]` adoption ([#269](https://github.com/MintPlayer/MintPlayer.Spark/pull/269),
`preview.53`) for this repo.

`IndexRegistry.RegisterIndex` guards on `_byIndexName` and then assigns
`_byCollectionType[collectionType]` **unconditionally**
([`IndexRegistry.cs:88`](https://github.com/MintPlayer/MintPlayer.Spark/blob/023ec43b097a338e2dcc801119a32ec4d6823185/libs/spark/MintPlayer.Spark/Services/IndexRegistry.cs#L88)).
Two differently-named indexes over the same entity therefore both register, and the collection ends up
bound to whichever `Assembly.GetTypes()` reached last — no build signal, no startup signal.

`SPARK_INDEX_004` is the only thing preventing it today, and it does not cover the general case: it
sees one compilation, so it catches a generated index colliding with a hand-written one but not two
hand-written indexes, nor a collision across assemblies contributed via `AddIndexesFrom(...)`. It is
also an analyzer diagnostic, so `.editorconfig` can switch it off and get the coin flip instead of a
clean failure. Its own doc-comment describes the runtime as "keys registrations by collection type and
silently skips duplicates", which is not what the code does — the practical difference being that the
index which stops serving queries is the one you did not touch.

Underneath sits a design question the issue also raises: `_byCollectionType` is a
`Dictionary<Type, IndexRegistration>`, so Spark structurally cannot represent more than one index per
entity, while RavenDB treats several indexes over a collection as routine. That is what blocks a
generated `VCommit` coexisting with our hand-written `Commits_ByRepository` — see
`docs/adopt-generated-indexes.md` §2. Proposed upstream: guard `_byCollectionType` the way
`_byIndexName` is guarded (deterministic first-wins), fix the doc-comment, and optionally allow
coexistence by making the collection binding an *explicit* default rather than an implicit one.

**Not blocking us.** Our plan routes around it entirely by persisting `Commit.Date` and
`Commit.HasCoverage` so one generated index replaces the hand-written one.

**✅ SHIPPED, then superseded.** `preview.55` (PR #278) made the registry retain every index with a
deterministic ordinal-min default — which fixed the coin flip but left resolution *ambient*, and a
query-declared `indexName` was still silently overridden. That gap was filed as
[#279](https://github.com/MintPlayer/MintPlayer.Spark/issues/279) and shipped in `preview.56` (PR #280):
`IIndexRegistry` is **deleted** in favour of a name-keyed `IIndexCatalog`, a query's `indexName` is
authoritative, and a new typeless `[DefaultIndex]` on the index class elects the projection that shapes an
entity's model file (`[GenerateIndex]` emits it; analyzer SPARK009 guards duplicates). Two rules worth
keeping: the election only considers **projection-bearing** indexes, so a projection-less hand-written
index like `Commits_ByRepository` is invisible to it (and `[DefaultIndex]` **on** such an index throws at
startup); and the design record is `docs/spark-issue-279-PRD.md`.

## 1. Bug: typed webhook messages produce invalid queue names (likely High)

**Symptom**: any app with a typed `IRecipient<GitHubWebhookMessage<TEvent>>` faults at
startup: the worker's queue-name allowlist rejects the name.

**Cause** (verified in source):
- `MessageBus.StoreMessageAsync` (libs/messaging/MintPlayer.Spark.Messaging/Services/MessageBus.cs:34-36)
  and `MessageSubscriptionManager.DiscoverQueueNames` (Services/MessageSubscriptionManager.cs:107-108)
  both fall back to `messageType.FullName` when there's no `[MessageQueue]`.
- For a closed generic, `FullName` embeds assembly-qualified args:
  `Ns.GitHubWebhookMessage`1[[Octokit.Webhooks.Events.PullRequestEvent, Octokit.Webhooks, Version=…]]`
  — contains `[ ] , =` and spaces.
- `MessageSubscriptionWorker.IsValidQueueName` (Services/MessageSubscriptionWorker.cs:60-73,
  added by R2-H14) rejects those chars; `ConfigureSubscription` throws at startup, faulting
  the manager via `Task.WhenAll`.
- `GitHubWebhookMessage<TEvent>` (libs/webhooks/.../Messages/GitHubWebhookMessage.cs:28) has
  no `[MessageQueue]`; only the non-generic catch-all does (`spark-github-all`).

**Proposed fix** (drafted, reviewed design): a single internal `QueueNames` class in
`MintPlayer.Spark.Messaging/Services` owning both derivation and validation:

```csharp
internal static class QueueNames
{
    public static string ForMessageType(Type messageType)
    {
        var attr = messageType.GetCachedCustomAttribute<MessageQueueAttribute>();
        return attr?.QueueName ?? Derive(messageType);
    }

    // FullName of a closed generic embeds assembly-qualified args whose
    // '[', ']', ',', '=' and spaces IsValid rejects. Derive from the definition's
    // FullName + recursively-derived arg names so every CLR type yields a valid,
    // deterministic name (both bus and manager derive identically).
    private static string Derive(Type type)
    {
        if (!type.IsGenericType) return type.FullName!;
        var definitionName = type.GetGenericTypeDefinition().FullName!;   // "Ns.Message`1"
        var argumentNames = string.Join("-", type.GetGenericArguments().Select(Derive));
        return $"{definitionName}-{argumentNames}";
    }

    public static bool IsValid(string value) { /* move IsValidQueueName here verbatim */ }
}
```

- Use in `MessageBus.StoreMessageAsync` and `MessageSubscriptionManager.DiscoverQueueNames`
  (replacing both `FullName` fallbacks) and `MessageSubscriptionWorker.ConfigureSubscription`
  (calls `QueueNames.IsValid`).
- Non-generic names are unchanged (`FullName`, incl. nested `Outer+Inner`) — the existing
  `MessageBusTests.BroadcastAsync_persists_a_SparkMessage_with_inferred_queue_name_and_payload`
  stays green.
- Tests to add (xunit + FluentAssertions, `tests/MintPlayer.Spark.Tests/Messaging/`,
  InternalsVisibleTo already present): closed-generic type → name passes `IsValid`;
  bus + manager agree for the same closed generic; `[MessageQueue]` still wins; nested
  generic args. Repro/regression: boot check or E2E asserting a typed
  `IRecipient<GitHubWebhookMessage<PullRequestEvent>>` app starts and receives events.
- Cleanup while there: `Messages/GitHubQueueNames.cs` is dead code (zero call sites) and
  the README queue tables (libs/webhooks/.../README.md:258-262, 481-493) describe that
  abandoned scheme — delete/fix.

## 2. New library: API tokens (PAT) for CI upload authentication

Spark has zero API-token infrastructure (verified). Coverage implements it app-locally
first (namespace `Coverage.ApiTokens`, designed for extraction); once stable, lift it into
e.g. `MintPlayer.Spark.Authorization.ApiTokens`:

- Token document: SHA-256(token) as document id (unique by construction), scope claims,
  created-by, optional expiry, revocation timestamp. Value = prefix + 256-bit urlsafe
  random, shown once.
- `AuthenticationHandler` resolving `Authorization: Bearer <prefix>…` / `Token <prefix>…`
  to a `ClaimsPrincipal` with scope claims; registered via the existing
  `configureProviders: Action<IdentityBuilder>` hook (SparkBuilderExtensions.cs:26-42).
- Issuance/list/revoke endpoints under `/spark/auth/tokens` (cookie-authenticated,
  XSRF-protected, IEndpointBase pattern like Logout.cs).
- Keep the scope vocabulary app-defined (library stays domain-agnostic).

Check MintPlayer/CodeCoverage for the app-local implementation to extract (M2 will add it
under `Coverage/ApiTokens/`).

## 3. Bug: external-login popup handshake never fires

- The callback only returns the `postMessage` HTML when `?popup` is on the **callback**
  (SparkAuthenticationExtensions.cs:208), but `/spark/auth/external-login` builds the
  callback URL without propagating it (:118). Demo opens the popup without `&popup=1`
  anyway (WebhooksDemo shell.component.ts:55-73) and leaks the message listener on the
  redirect path.
- Fix: propagate `popup` from external-login to the callback URL; demo: pass `popup=1`
  and remove the listener on failure paths too.
- Coverage works around it with a full-page redirect (no popup) — no urgency, but the
  feature is broken as shipped.

## 4. ng-bootstrap bump: 22.4.0 → 22.13.x

- Root package.json pins `@mintplayer/ng-bootstrap` 22.4.0; latest is 22.13.x, spanning the
  web-component rearchitecture.
- New peers to install in the workspace: `@mintplayer/web-components ^2.0.0`, `lit ^3.3.0`,
  `@mintplayer/ng-click-outside ^22.0.0`, `@mintplayer/ng-focus-on-load ^22.0.0`.
- Known breaking change hitting the demos: `<bs-accordion-tab-header>` component →
  `[bsAccordionTabHeader]` attribute directive (used in every demo shell sidebar).
  `@mintplayer/ng-swiper` was deleted upstream. Scheduler: `event-click` → `event-selected`.
- ng-spark/ng-spark-auth peer ranges (`^22.4.0` / `^22.2.0`) already admit 22.13 — this is
  about actually building/testing against it and republishing.
- Nice-to-have while there: promote WebhooksDemo's `BsShellTopbarDirective`
  (ClientApp/src/app/shell/bs-shell-topbar.directive.ts — "TODO: promote to
  @mintplayer/ng-bootstrap/shell") — though the component itself belongs in the
  ng-bootstrap repo, the demos can drop their copies after.

## 5. Decision: R4-H1 (open security finding, High)

Row-level authorization is enforced on `/spark/po` but NOT on `/spark/queries/{id}/execute`
or the WebSocket `/stream` path (docs/prd/PRD-SecurityAudit-Round4-Plan.md). Coverage
sidesteps it by running DenyAll + custom /api endpoints, but any multi-tenant Spark app
inherits it. Decide: fix in this PR (filter query/stream results through
`IsAllowedAsync` like DatabaseAccess does) or track separately.

## 6. Cheap doc fixes (opportunistic)

- README methods that don't exist: `CreateClientAsync` → `CreateInstallationClientAsync`
  (webhooks README:226,234); `UseSparkAntiforgery` (authorization README:220,284);
  `AddSparkAuthorization`/`AddSparkAuthentication`/`MapSparkIdentityApi` documented as
  public but internal.
- `AllowedDevUsers` empty-list semantics: docs say "empty = allow all", code fails closed
  (GitHubWebhooksOptions.cs:30 vs SparkBuilderExtensions.cs:101).
- `ClientSecret` documented as a webhook option but absent from `GitHubWebhooksOptions`.
- Stale queue-name tables (see item 1).
- README claims Angular 21; workspace is Angular 22.

## Verification found during out-of-tree consumption (FYI, no action yet)

- `dotnet build` of an out-of-tree app referencing the published
  `10.0.0-preview.41` packages works, including source generators and the
  Authorization package's npm auto-install of `@mintplayer/ng-spark-auth` +
  generation of `spark-auth.setup.ts`. First real PackageReference consumer = the
  MintPlayer/CodeCoverage repo.
