# PRD — Spark hardening for the Coverage integration (M0)

**Status:** Draft for review
**Date:** 2026-08-07
**Author:** Investigation team (6 parallel investigators) + synthesis
**Origin:** `C:\Repos\Coverage\docs\spark-handoff.md`, generated during the Coverage-analyzer investigation. Cross-checked against Coverage's own `docs/PRD.md` §10 and `docs/PLAN.md` M0.
**Base:** `master` @ `febea26`. The handoff asked to "confirm intended base" because it was written against a `security-audit` checkout; that state is older than current `master`, so `master` is the base.
**Scope:** `MintPlayer.Spark.Messaging`, `MintPlayer.Spark.Authorization` (+ a new `…Authorization.ApiTokens` package), `MintPlayer.Spark.Webhooks.GitHub` (docs), the four demo ClientApps, root `package.json`.
**Backward compatibility:** Not required — framework is in preview (`10.0.0-preview.41`). We may rename/replace existing shapes.

---

## 0. Summary

Six work items surfaced by the first real out-of-tree consumer of the published Spark packages (MintPlayer/CodeCoverage). Every claim in the handoff was verified against source; **four were confirmed, three were materially wrong or mis-scoped**, and one bug turned out to be worse than reported.

| # | Item | Handoff said | Verified verdict | In this PR? |
|---|---|---|---|---|
| 1 | Generic message types → invalid queue names | High | **Confirmed, and worse** — flagship demo is broken at HEAD | ✅ Yes |
| 2 | API tokens (PAT) library | Extract from Coverage later | **Sequencing reversed** — Coverage's own plan puts it in Spark *first*; prior art to port appeared mid-investigation | ✅ Yes |
| 3 | External-login popup handshake | Propagate a query param | **Confirmed, wider** — all failure paths broken too | ✅ Yes |
| 4 | ng-bootstrap 22.4.0 → 22.13.x | New peers + attribute directive | **Mostly wrong** — peers already present; it's a *structural* directive | ✅ Yes |
| 5 | R4-H1 row-level authz gap | Decide: fix or track | **Confirmed, plus a second bypass** — row-level only; type-level *is* enforced | ✅ Yes |
| 6 | Cheap doc fixes | 5 items | Confirmed, +2 more found | ✅ Yes |

### How Coverage actually uses Spark — and what that means for priorities

Verified by reading Coverage's backend directly (it is much further along than the handoff implies — six controllers, a working ingestion pipeline, upload storage, and a tree endpoint all exist).

**Coverage uses Spark as an auth + messaging + cron + webhooks substrate, not as a UI framework.** Every controller queries the raw `IAsyncDocumentSession`; repo-wide there are **zero** references to `IDatabaseAccess`, `SparkQuery`, `EntityTypeDefinition`, or `SparkStreamingService`. `Program.cs:38-42` denies Spark's generic data endpoints outright, stating the app routes everything through its own `/api` controllers. What it *does* consume: `SparkContext` + model sync, `[Breadcrumb]`/`[Reference]` attributes, **`IMessageBus` + recipients**, **`MintPlayer.Spark.Cron`**, GitHub webhooks/installations, and `SparkUser`/`UserManager` Identity.

That re-ranks this PR's items against the stated goal:

| Item | Value to Coverage |
|---|---|
| **1 — queue names** | **Critical and blocking.** Coverage's ingestion pipeline *is* Spark messaging (`UploadsController` → `BroadcastAsync` → `ParseSessionRecipient` → `FinalizeBuildRecipient`), and it registers GitHub webhook recipients. This bug takes the host down. |
| 6 — docs | Real: the wrong method names are ones Coverage calls. |
| 2 — API tokens | **Not blocking** — Coverage already has a working `ApiTokenAuthenticationHandler` + `TokensController`. The library is consolidation, not enablement. |
| 3 — popup | Low: Coverage uses a full-page redirect; its own doc says "no urgency". |
| 4 — ng-bootstrap | Unrelated to Coverage. |
| 5 — row-level authz | **Zero value to Coverage** — `Program.cs:38-42` explicitly sidesteps it via DenyAll. Justified by Fleet and WebhooksDemo, not by this consumer. |

**Item 1 is the only thing here on Coverage's critical path.** The rest is framework health — worth doing, but it should not be mistaken for unblocking the coverage website.

**Not a Spark gap:** file upload/download. Coverage handles multipart with plain ASP.NET (`[FromForm] IFormFileCollection`, 50 MB limit) and stores raw reports as **RavenDB attachments** on the `Build` document. Spark needs no file primitives for this; the app layer is the right place.

### What this PR does *not* deliver toward the coverage website

Coverage's Angular ClientApp already exists and implements most of the target experience with **hand-written** pages (`app.routes.ts:12-17`: `/a/:login`, `/r/:owner/:repo`, `/r/…/c/:sha`, `/r/…/c/:sha/f`). It consumes only a thin slice of `@mintplayer/ng-spark` — `sparkRoutes()`, the retry-action modal, i18n pipes, `SparkLanguageService`, `SparkAuthService`. `sparkRoutes()` is mounted but unreachable: nothing links to `/po/` or `/query/`.

Against the stated goal, status is:

| Capability | State |
|---|---|
| GitHub login (Identity-backed) | ✅ working (full-page redirect; **item 3 would let it use the popup** — `shell.component.ts:38-41` documents the workaround) |
| Orgs → repos browsing | ✅ built |
| Repo tree, coverage per folder/file | ✅ built (`bs-table` + drill-down, one server round-trip per folder) |
| File view, per-line covered/uncovered tinting | ✅ built (`file.component.scss:13-23`) |
| **Syntax highlighting** | ❌ **absent.** No highlighter is installed. `file.component.ts:19-23` parks it on "the syntax-highlighting `bs-code-viewer` once it ships in mintplayer-ng-bootstrap" — i.e. blocked on a **third repo**, not on Spark. |
| **Nested circular / sunburst diagram** | ❌ **absent.** No charting dependency at all; the only coverage visual is a linear `bs-progress` bar. Needs a new dependency or hand-written SVG, in the Coverage app. |

**Neither gap is a Spark gap, and neither is in this PR's scope** — but they are the only two things standing between Coverage and the stated goal, so they need owners elsewhere.

**Cross-repo version note (relevant to item 4):** Coverage's ClientApp already pins `@mintplayer/ng-bootstrap ^22.13.0` while this workspace is on `^22.4.0`, alongside `ng-spark ^22.0.8` / `ng-spark-auth ^22.0.1`. Peer ranges admit the combination, but Spark's libraries are currently only *validated* against 22.4 — which is a concrete argument for item 4 beyond housekeeping.

Scaling risks worth flagging to the Coverage team (not this PR): the file view renders one DOM node per source line with no virtualization; the tree endpoint re-streams and re-aggregates the whole build's `FileCoverage` set on every folder drill-down; and there is no live refresh, so an in-flight build's status never updates without a manual reload.

---

## 1. Typed message queue names (High — actively breaking)

### Problem

`MessageBus.StoreMessageAsync` (`MessageBus.cs:34-36`) and `MessageSubscriptionManager.DiscoverQueueNames` (`MessageSubscriptionManager.cs:107-108`) both fall back to `messageType.FullName` when a message type carries no `[MessageQueue]`. For a **closed generic**, `FullName` embeds assembly-qualified type arguments:

```
Ns.GitHubWebhookMessage`1[[Octokit.Webhooks.Events.PullRequestEvent, Octokit.Webhooks, Version=…]]
```

That contains `[`, `]`, `,`, `=` and spaces. `MessageSubscriptionWorker.IsValidQueueName` (`MessageSubscriptionWorker.cs:60-73`) rejects them, so `ConfigureSubscription` throws.

`GitHubWebhookMessage<TEvent>` (`GitHubWebhookMessage.cs:28`) has no `[MessageQueue]`; only the non-generic catch-all does (`spark-github-all`).

**Correction to the handoff:** backticks and `+` are *already* allowed by the validator (see the comment at `MessageSubscriptionWorker.cs:65-71`), so generic-arity markers and nested-type separators are fine. The rejected characters are exactly the ones an assembly-qualified name introduces.

### Why it went unnoticed — and why it's worse than reported

The fault is **asynchronous**. Broken workers throw *before* any `await`, so their `ExecuteAsync` completes synchronously faulted. But WebhooksDemo also registers `LogAllWebhooks : IRecipient<GitHubWebhookMessage>`, whose non-generic message type has a valid `[MessageQueue("spark-github-all")]`. That worker blocks on a real Raven network call, so `Task.WhenAll` (`MessageSubscriptionManager.cs:52`) cannot resolve synchronously, `_executeTask.IsCompleted` is `false`, and `BackgroundService.StartAsync` returns `Task.CompletedTask`. **The host starts cleanly and serves traffic.** Only when the valid subscription's I/O completes does `WhenAll` observe the faulted siblings and, under the .NET 6+ default `BackgroundServiceExceptionBehavior.StopHost`, trigger `StopApplication()`.

So it presents as "app started fine, then went dark" — not "app won't start."

**Dating (a false lead, recorded so nobody re-walks it):** `applogs.log:60-95` shows a WebhooksDemo deployment failing healthchecks on 2026-03-22 in exactly that shape. It is **not** this bug. The throwing validator arrived in `ae37fed` on **2026-06-06** ("Security Audit Round 2"); before that, `ConfigureSubscription` merely quote-escaped the name, and a malformed RQL query degraded to a logged warning via the pre-existing `CreateAsync`→`UpdateAsync`→`LogWarning` fallback — never a crash, in any historical version. The March log predates the validator by ~2.5 months and is most likely the RavenDB-connectivity incident visible in `docker.log`, fixed by `22330b3`. Discard it.

**The real timeline:** the bug has been live on `master` since **2026-06-06**, roughly two months. It was introduced *by the security-hardening pass itself* — tightening a permissive escape into a fail-fast allowlist without auditing the callers that could hand it a closed-generic type name. Verified at HEAD from code alone: the throwing validator (`MessageSubscriptionWorker.cs:50-52,60-73`), both `FullName` fallbacks, the missing attribute on `GitHubWebhookMessage<TEvent>`, and five closed-generic recipients in `Demo/WebhooksDemo/WebhooksDemo/Recipients/` are all simultaneously present.

**The severity-sharpening consequence:** the delayed presentation is an accident of WebhooksDemo happening to have a valid queue in the mix. An app with *only* generic recipients and no catch-all faults synchronously inside `Host.StartAsync()` and never serves at all — a hard boot failure. Coverage, as the first out-of-tree consumer, is in the worse case.

Verified: no allow-list gates this (`MessageTypeAllowList` feeds only `ProcessBatchAsync`'s deserialization guard), recipients are registered as genuine closed generics by `RecipientRegistrationGenerator.Producer.cs:42`, and `BackgroundServiceExceptionBehavior` is unconfigured repo-wide.

### The constraint that determines the design

`MessageSubscriptionManager` derives its queue name **independently**, by reflecting over `IRecipient<>` registrations, and `MintPlayer.Spark.Messaging` has no dependency on `MintPlayer.Spark.Webhooks.GitHub`. A producer-side override therefore *could not* make the two agree. **The fix must live in the Messaging layer** — that is the only place both sides can compute the same name from reflection alone.

This also makes `GitHubQueueNames.cs` unresurrectable: it cannot make the consumer side agree, so there is no point wiring it in. **Delete it.**

### Design

A single internal `QueueNames` class in `MintPlayer.Spark.Messaging/Services` owning both derivation and validation, so the two cannot drift. Derivation is **one recursive function**, not a generic-vs-non-generic branch:

```csharp
private static string SafeName(Type t)
{
    if (!t.IsGenericType)
        return t.FullName!;                                   // base case

    var def  = t.GetGenericTypeDefinition().FullName!;         // "Ns.Message`1" — backtick+digit already allowed
    var args = string.Join("-", t.GetGenericArguments().Select(SafeName));  // recurse
    return $"{def}-{args}";
}
```

Why recursion rather than a special case:

- **Non-generic types keep `FullName` byte-for-byte** — that's the base case, so it falls out of the recursion rather than needing a compat carve-out. The "keep FullName for non-generics" and "one uniform scheme" options collapse into the same thing.
- **Call sites stop branching.** `MessageBus` and `MessageSubscriptionManager` both just call it unconditionally — one path, no "is this generic?" reasoning at either site, which is what makes the two agreeing self-evident.
- **Nested generics are handled.** An earlier sketch used each argument's simple `Type.Name`, which silently mis-derives `Foo<Bar<Baz>>`. Recursing is both simpler and more correct — and it avoids the namespace-collision caveat that the simple-name approach would have forced us to document.
- **Invalid names become structurally impossible.** `[`, `]`, `,`, `=` and spaces enter only via `FullName`'s assembly-qualified rendering of *constructed* generics. By never calling `.FullName` on a constructed generic, and composing from the definition's plain name plus recursively-safe arguments joined with `-` (never `,`), every recursion depth is safe by construction.

`ForMessageType` returns `attribute?.QueueName ?? SafeName(type)`, cached per `Type` via the existing `ReflectionCache` pattern — this runs per broadcast and per discovery scan. A final defensive pass replaces any residual disallowed character with `_`, as cheap insurance against runtime-emitted types (dynamic/EF proxies, reflection-emit). `IsValid` stays a hard-fail guard for **developer-supplied** values only (`[MessageQueue("…")]`, explicit `BroadcastAsync` overrides), where failing fast on a typo is correct.

### Risks

**There is nothing to migrate**, in either direction:

- Non-generic types keep an unchanged name (the recursion's base case).
- Closed generics never had a working queue in *any* historical version. Post-`ae37fed` the worker throws before `Subscriptions.CreateAsync` ever runs; pre-`ae37fed` the malformed RQL failed `CreateAsync`, then failed `UpdateAsync`, and was swallowed into a warning. Either way no Raven subscription was created and no `SparkMessage` was durably stuck under a generic-derived name.

So an app upgrading past this fix needs no queue draining and no subscription recreation. Recorded because the no-compat constraint made it a free choice regardless — it did not drive the design.

---

## 2. API tokens (PAT) for CI upload authentication

> ⚠️ **Superseded in part by decision D1 (2026-08-08).** The machine credential is now OAuth2 **`client_credentials`** via `MintPlayer.Spark.IdentityProvider` (ported in `d51f9fd`), not a bespoke PAT library — one credential subsystem for machine callers rather than two. D1 was made conditional on the package being proven sound; that audit is **[findings-identity-provider-audit.md](./findings-identity-provider-audit.md)**, which found 4 Critical and 6 High issues including a one-click account takeover. All eleven are fixed; 25 remain open and are sequenced as M12.4 in the plan.
>
> The section below is retained because its design reasoning still holds — in particular the credential-to-claims seam, the scope→group mapping decision, and the `NoResult()` discipline that lets schemes coexist. Only the *choice of credential* changed.

### Sequencing: the handoff has it backwards; prior art: it now exists

The handoff says Coverage builds this app-locally first and Spark extracts it later. But Coverage's `PLAN.md` puts `MintPlayer.Spark.Authorization.ApiTokens` in **M0** — a Spark-side PR landing *before* Coverage's scaffolding (M1) and ingestion (M2), with M2 explicitly consuming "the M0 lib." M0's exit criterion is *"a demo app can mint and authenticate with an API token; Spark tests green"* — self-validating in Spark. Build it in Spark now; don't wait on Coverage.

> **Note on freshness.** Coverage is under active development *concurrently with this PRD*. As of 12:40 on 2026-08-07 the only token artifact was an untracked stub; by 14:42 a working `Coverage/ApiTokens/` had appeared. **Re-read that directory before starting M4** — it may have moved again.

There is now **working prior art to lift** (`C:\Repos\Coverage\Coverage\ApiTokens\`), and it settles the design's open questions empirically rather than by assumption. Spark itself still has zero PAT infrastructure, and `MintPlayer.Spark.IdentityProvider` is not on `master`, so nothing is reusable in-repo.

**Lift these decisions directly** — they're already proven against a real Spark app:

- **Token format** (`ApiTokenService.cs:14-21`): `covt_` + base64url of 32 random bytes (`RandomNumberGenerator.GetBytes(32)`, `+`→`-`, `/`→`_`, trailing `=` trimmed). Prefix must be **configurable** in the library.
- **Hashing** (`:23-24`): `Convert.ToHexStringLower(SHA256.HashData(...))`, hash is the document id (`ApiTokens/{hash}`). No constant-time compare needed — it's a point-load by id, and the stored value is a hash of the secret, so a DB leak leaks no credentials.
- **Cheap pre-filter** (`:26-27`): `LooksLikeToken` checks prefix + length *before* hashing.
- **`NoResult()`, not `Fail()`, for non-matching headers** (`Handler:40,44,48`) — this is the important one. Returning `NoResult` when the header is absent, isn't `Bearer`/`Token`, or doesn't carry our prefix lets **cookie and other bearer schemes still try**. That is precisely the multi-scheme coexistence problem noted below, already solved. `Fail()` is reserved for a token that *is* ours but is unknown or revoked.
- **Claims shape** (`Handler:57-66`): namespaced claim types (`covt:scope`, `covt:account`, `covt:repoid`, `covt:hash`), only emitting the optional ones when present.
- Plain `AuthenticationHandler<AuthenticationSchemeOptions>` — no bespoke options class was needed.

### Design

New package `libs/authorization/MintPlayer.Spark.Authorization.ApiTokens/`, sitting beside the Authorization package the way the webhooks packages do. PAT auth is orthogonal to cookie+password+2FA+external-login and shouldn't bloat the core package's surface.

```
Identity/SparkApiToken.cs                        // id = "ApiTokens/{sha256-hex}", Prefix, Scopes, CreatedBy, CreatedOn, ExpiresOn, RevokedOn
Authentication/ApiTokenAuthenticationOptions.cs
Authentication/ApiTokenAuthenticationHandler.cs
Extensions/ApiTokenBuilderExtensions.cs          // AddApiTokens(this IdentityBuilder, …)
Services/IApiTokenService.cs / ApiTokenService.cs
Endpoints/ApiTokensGroup.cs                      // Prefix "/spark/auth/tokens"
Endpoints/{IssueToken,ListTokens,RevokeToken}.cs
```

Token value = prefix + 256-bit urlsafe random, shown once. SHA-256 of the value is the document id, so uniqueness holds by construction and lookup is a point-load. Persisted via a dedicated store over `IAsyncDocumentSession` (mirroring `UserStore`/`RoleStore`), **not** through the `PersistentObject` CRUD pipeline — these are never edited through the generic model.

Management endpoints (issue/list/revoke) are cookie-authenticated and antiforgery-stamped directly on the endpoint, as `Logout.cs:14` and `CsrfRefresh.cs` do — cleaner than the `antiforgeryGatedRoutes` retrofit that `MapIdentityApi`'s canned routes need.

**Naming:** Coverage's docs use four names for one concept (`UploadToken`, `SparkApiToken`, `ApiToken`, `Coverage.ApiTokens`). The *library* name is consistent everywhere. We take `MintPlayer.Spark.Authorization.ApiTokens` + document type `SparkApiToken` and note the churn back to Coverage.

### Risks — two real ones

1. ~~**`IdentityBuilder` has no `.AddScheme<>()`.**~~ **Resolved — and the premise was wrong.** Both the handoff and Coverage's `PRD.md:145` say the handler is "wired via the existing `configureProviders: Action<IdentityBuilder>` hook." **Coverage's working implementation does no such thing.** It registers the scheme *outside* `AddSpark` entirely (`Coverage/Program.cs:83-84`):

   ```csharp
   builder.Services.AddAuthentication()
       .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
           ApiTokenAuthenticationHandler.SchemeName, null);
   ```

   with the comment at `:81` calling it "Registered as an extra scheme" — deliberately separate from the Identity pipeline. So the `IdentityBuilder` → `AuthenticationBuilder` indirection is **sidestepped, not solved**: a PAT scheme is orthogonal to Identity's provider configuration and doesn't belong on that hook at all. `AddApiTokens()` should therefore be an `ISparkBuilder`/`IServiceCollection` extension doing exactly the above, **not** an `IdentityBuilder` extension. This removes M4's largest unknown and simplifies the design.

2. **Scope claims are not groups.** `ClaimsGroupMembershipProvider.cs:19-26` reads only `"group"`/`"groups"`/role claim types off `HttpContext.User` — it never touches `SparkUser`, `UserManager`, or RavenDB. So a token principal flows through authorization fine *as long as it carries a matching claim*; being a non-`SparkUser` is genuinely not the problem. But scopes like `read:cars` are not group names. Either the handler maps scopes onto group claims, or a custom `IGroupMembershipProvider` is registered via the existing `AddGroupMembershipProvider<TProvider>()` (`SparkAuthorizationExtensions.cs:66-78`). **This is a required design decision, not something that "just works."**
3. Three coexisting schemes (Application cookie, Identity Bearer, ApiToken) require explicit per-endpoint scheme selection. **Largely answered by the `NoResult()` discipline above** — a well-behaved handler that abstains on headers that aren't its own composes with the others without per-endpoint configuration. Still verify the combination under test rather than assuming.

Scope vocabulary stays app-defined; the library stays domain-agnostic.

### Forward consideration — a third scheme is coming, and it is *not* the IdentityProvider

Coverage's `POST /api/uploads` accepts **either** a `covt_`-prefixed PAT **or** a GitHub Actions OIDC JWT (`PRD.md:165`). The tokenless path (`PRD.md:142`, `PLAN.md:84` — Coverage's **M7**, well after M0) validates the workflow's ID token as a **standard JWT bearer against GitHub's JWKS** (`Authority = https://token.actions.githubusercontent.com`, `aud` == the service URL, repo resolved from the `repository`/`repository_owner` claims).

That is `Microsoft.AspNetCore.Authentication.JwtBearer` pointed at GitHub — **Spark as an OIDC *client/verifier***. It has nothing to do with `MintPlayer.Spark.IdentityProvider`, which is Spark acting *as* an OIDC **provider** issuing tokens to other applications. Verified: Coverage never issues tokens to third parties and never hosts `/authorize`, `/token`, or a discovery document. **The IdentityProvider package is not required for Coverage** and should stay on its unmerged branch.

Two design consequences for this package:
- Don't over-fit to a single scheme. The `/api/uploads`-style endpoint must eventually accept **two** schemes, so per-endpoint scheme selection (already flagged as risk 3) needs to work for a multi-scheme policy, not just one.
- The token prefix should be **configurable** (Coverage wants `covt_`), not hardcoded.

---

## 3. External-login popup handshake

### Verified

`/spark/auth/external-login` builds the callback URL at `SparkAuthenticationExtensions.cs:118` with no `popup` — it doesn't even accept the parameter. The callback checks `Request.Query.ContainsKey("popup")` at `:208` to choose the postMessage branch over `Results.Redirect` at `:224`. **Nothing ever sets it, so the postMessage branch is dead code** and every real popup flow navigates the popup window itself, never notifying the opener.

**Correction to my own initial concern:** provider `redirect_uri` exact-match is *not* a constraint here. `options.CallbackPath = "/signin-github"` (`GitHubAuthenticationExtensions.cs:31`) is what's registered with the provider and sent as the literal `redirect_uri`. The line-118 URL only becomes `AuthenticationProperties.RedirectUri`, which ASP.NET Core already encrypts into OAuth `state`. Carrying the flag through `state` by hand would be strictly more code for zero correctness gain. **Plain query-string propagation is the correct answer**, not merely the compatible one.

### Wider than reported

All three callback failure paths — `info is null` (`:139`), unverified/missing email (`:167`), `CreateAsync` failure (`:181`) — call `Results.Redirect` unconditionally, ignoring `popup`. Fixing only the success path would leave the opener's listener leaking on every cancellation or error.

`targetOrigin` is already `window.location.origin`, not `'*'` — **refuted** as a security issue.

### Design decision: library-owned, not a demo patch

`@mintplayer/ng-spark-auth` has **no external-login code at all** (`SPARK_OIDC_PROVIDERS`/`provideSparkOidcLogin()` are planned, not implemented). The sole consumer repo-wide is `Demo/WebhooksDemo/.../shell.component.ts:55-73`, which hand-rolls `window.open` + a `message` listener, hardcodes `/spark/auth/...` instead of using `config.apiBasePath`, and removes the listener only from inside itself — so a user closing the popup manually leaks it forever.

Two options:

- **(a) Minimal:** propagate `popup` server-side, make failure branches popup-aware, add `&popup=1` and listener cleanup in the demo.
- **(b) Library-owned (recommended):** all of (a), plus a new `SparkAuthService.loginWithProvider(provider, { returnUrl, mode })` that absorbs `window.open`, listener add/remove, manual-close detection via a `closed` poll, the config-driven URL, and the post-login `checkAuth()`. WebhooksDemo collapses to a one-line call with no `window.open` in app code.

Recommend **(b)**. It's a deeper module — the interface is two arguments and it hides the entire handshake — and it makes the leak unreachable for future consumers rather than a per-caller discipline problem. The no-compat constraint makes the payload change free.

Message contract becomes `{ type: 'spark:external-login', success: true }` / `{ …, success: false, error? }`, replacing the ad hoc `'external-login-success'` string.

### Risks

`loginWithProvider` is purely additive to `ng-spark-auth`'s public surface (nothing called the old pattern through the library — there wasn't one), so a minor bump suffices. The postMessage payload shape changes, but its only consumer is rewritten in the same PR. **No OAuth provider-side registration changes anywhere.**

---

## 4. ng-bootstrap 22.4.0 → 22.13.0

### The handoff is mostly wrong here

- **"New peers to install"** — no. `@mintplayer/web-components ^2.0.0`, `lit ^3.3.0`, `@mintplayer/ng-click-outside ^22.0.0`, `@mintplayer/ng-focus-on-load ^22.0.0` are already peers of the *installed* 22.4.0 and already present in `node_modules` at satisfying versions (web-components 2.0.0, lit 3.3.3, both others 22.0.0). The web-component rearchitecture landed **before** 22.4.0. The only peer delta is `@mintplayer/ng-swiper` being **dropped**; it's unused in Spark and survives only in `package-lock.json`.
- **"`<bs-accordion-tab-header>` → `[bsAccordionTabHeader]` attribute directive"** — it's a **structural** directive: `<ng-container *bsAccordionTabHeader>`, plus swapping `BsAccordionTabHeaderComponent` → `BsAccordionTabHeaderDirective` in each component's `imports`. That makes it **8 files**, not 4.
- **Scheduler `event-click` → `event-selected`** — real (landed 22.11.0) but **unused in Spark**. Zero hits.

Root-caused against `C:\Repos\mintplayer-ng-bootstrap` by pinning the exact commit range (`a4abc015` = 22.4.0 → `799fa41a` = 22.13.0, only 14 commits) and grepping the conventional-commit breaking marker. **`CHANGELOG.md` is unreliable here** — its "Unreleased" section lists breaking entries that shipped *before* 22.4.0, and omits the accordion/swiper deletion entirely. There are **four** breaking commits, not three:

| Commit | Change | Landed | Effect on Spark |
|---|---|---|---|
| `17328c57` | Deletes the legacy `bs-navbar*` family | 22.5.0 | **Not in the handoff.** Zero hits repo-wide. Spark uses only `BsNavbarTogglerComponent` from the separate `navbar-toggler` subpackage — diffed across the range, **zero-line diff**. Harmless, but it's the largest deletion in the range and belongs on the record. |
| `207d85f7` | Accordion header component → directive; `ng-swiper` deleted | 22.6.0 | **The only one that touches Spark.** |
| `9c70d175` | Scheduler five view modes | 22.9.0 | Unused. |
| `a66f4439` | Scheduler rewrite; `event-click` → `event-selected` | 22.11.0 | Unused. |

### Blast radius — exactly 8 files

`Demo/{DemoApp,Fleet,HR,WebhooksDemo}/*/ClientApp/src/app/shell/shell.component.{ts,html}`. Identical mechanical diff in each.

No `overrides` changes needed. **No ng-spark / ng-spark-auth republish** — neither library's source uses any changed symbol, and their caret peer ranges (`^22.4.0`, `^22.2.0`) already admit 22.13.0.

### Risk

Low, but not zero: headers now render into a named shadow-DOM slot rather than light-DOM projection. The outward migration is mechanical; the internal change is not. **Each of the four demo sidebars needs a visual expand/collapse check.**

`BsShellTopbarDirective` is duplicated in **all four** demos (not just WebhooksDemo), and no upstream replacement has shipped in 22.13 — its "promote to ng-bootstrap" TODO stays **out of scope**; it needs an upstream contribution first.

---

## 5. R4-H1 — row-level authorization on query-execute and `/stream`

### Confirmed, but narrower than stated

First, a documentation note: **`R4-H1` and the cited `docs/prd/PRD-SecurityAudit-Round4-Plan.md` do not exist in this repo.** Only `PRD-SecurityAudit.md` and `PRD-SecurityAudit-TestResults.md` are present, and neither mentions the identifier. The finding was verified directly against source instead.

Spark has **two distinct** authorization layers, and the handoff conflates them:

| Layer | Mechanism | `/spark/po` | `/queries/{id}/execute` | `/stream` |
|---|---|---|---|---|
| **Type-level** — may this caller query this entity type at all? | `IPermissionService.EnsureAuthorizedAsync("Query", …)` | ✅ | ✅ `QueryExecutor.cs:126,194` | ✅ `StreamingQueryExecutor.cs:50` |
| **Row-level** — may this caller see *this specific document*? | `IRowSecurity` → Actions' `IsAllowedAsync(action, entity)` | ✅ `DatabaseAccess.cs:430-438` | ❌ **none** | ❌ **none** |

So type-level authorization *is* enforced on both query paths — just one layer deeper than the endpoint, inside the executors rather than in `Execute.cs` (which contains no authorization calls of its own, unlike its siblings `Queries/List.cs:26` and `Queries/Get.cs:25`). The gap is **specifically and only row-level**: `IRowSecurity` has exactly two consumers repo-wide — `DatabaseAccess` and `BreadcrumbResolver.cs:121` — and neither query path is among them.

The consequence is real: an entity whose Actions class denies `Read` on individual rows is protected when fetched via `/spark/po`, and leaks through a query or a stream subscription over the same data.

### A second bypass, same root cause — `OnQueryAsync` is dead code

Declared at `Actions/IPersistentObjectActions.cs:18`, implemented at `Actions/DefaultPersistentObjectActions.cs:21`, and **never invoked** — no call site in `libs/`, no source-generator emission.

Two consequences, both bad:

1. `DatabaseAccess.cs:142` tells developers *"Callers that need a query-level filter for large collections can override `OnQueryAsync` directly."* **That advice is false**, and it points at exactly the scaling problem this fix runs into.
2. `Demo/WebhooksDemo/WebhooksDemo/Actions/GitHubProjectActions.cs:17` overrides it to enforce org-scoped access. Never called ⇒ **WebhooksDemo's list endpoint returns every project regardless of org membership.** The same class's `OnLoadAsync` (`:27`) enforces it *and is* called — so the author plainly believed both were live.

The root cause is architectural: each read path grew its own ad-hoc authorization, and two of four ended up with none. **Patching `QueryExecutor` alone would ship a "fix" that still leaves WebhooksDemo open** and leaves a comment directing developers to a dead hook.

### A third instance, on the *write* side

A separate investigation ([findings-replication-mtls.md](./findings-replication-mtls.md) F4) found the same root cause on a write path: `SyncApply.cs:83,107` → `SyncActionHandler.cs:41,61` → `SaveEntityViaActionsAsync` (`:237-248`) reflectively invokes `OnSaveAsync`/`OnDeleteAsync` **directly**, bypassing the `DatabaseAccess` chokepoint where every normal CRUD path calls `EnsureAuthorizedAsync` (`DatabaseAccess.cs:83,115,195,256`). An authenticated peer module can therefore write anything, anywhere.

Three bypasses, one cause: paths that don't route through a single enforcement point. **Design M5's chokepoint to cover writes as well as reads**, or this recurs. Whether the sync path is *migrated onto* it in this PR is a scope decision — the design should accommodate it either way.

### Fail-open branches on the already-"fixed" PO path

Five places return "allowed" when the check cannot be evaluated: `RowSecurity.cs:30` (hook not found), `DatabaseAccess.cs:169` (no readable `Id` on a projection → returns **everything** unfiltered), `:179` (empty id), `:181` (base document failed to load), `:441` (unknown hook shape). Rows three and four are live bypasses today: a `VCar` whose backing `Car` doesn't load is handed to a caller who may not be allowed to see it. With no compat constraint these flip to fail-closed — *unevaluable ≠ permitted*.

### Severity — lower than "High" for most apps

The default `IAccessControl` is **fail-closed**: `SparkMiddleware.cs:64` registers `DenyAllAccessControl`, with apps opting in explicitly (or to `AllowAllAccessControl` via `AllowAnonymousAccess()`, `SparkBuilderAnonymousAccessExtensions.cs:25`). So an unconfigured app denies everything rather than leaking.

To be vulnerable an app must simultaneously: configure authorization, **override `IsAllowedAsync(action, entity)` on an Actions class** to do per-row filtering, and expose that entity through a query or stream. That's a genuine multi-tenant hole for apps using row-level security, not a live hole in every Spark app. Coverage itself is unaffected — it runs DenyAll + custom `/api` endpoints.

**Rating: High for affected apps, Medium in aggregate** — the qualifier being that the affected population is exactly the apps that followed the framework's own documented row-security guidance.

Not lower than Medium, because it is **concretely exploitable in-tree today**: `Demo/Fleet/Fleet/Actions/CarActions.cs:33` restricts non-admins to cars they created; `GetCars` (`Car.json:267`, `source: "Database.Cars"`) is the primary Cars screen (`programUnits.json:14`); Fleet managers hold `QueryReadEditNew/Car` (`security.json:41`) so the type gate passes. **A Fleet manager on the app's main list sees every car, including the admin's — while `/spark/po` correctly hides them.** The existing test `tests/MintPlayer.Spark.E2E.Tests/Security/RowLevelAuthzTests.cs:71` passes only because it exercises `/spark/po`.

### Decision: in this PR

**Decided: this ships in the same PR as items 1–4 and 6.** The investigation recommended splitting it — it's 3–5 days against four modest fixes, it changes the public Actions contract, and its scope is the likeliest to move during implementation. That concern is recorded here and was raised; the call is to keep the bundle together, and the work below is planned on that basis.

Three things follow from bundling, and the plan accounts for each:

- **It dominates the diff.** Reviewers should read M5 as its own unit; the plan sequences it after the contained fixes so earlier commits stay independently reviewable and bisectable.
- **It needs its own release note** — a breaking `IPersistentObjectActions` change plus a behavior change for every row-scoped app. That note must not get buried under "fix popup."
- **The finding is undocumented.** No Round 4 doc exists and "R4-H1" is fabricated, so this PR also adds a properly-numbered entry to `docs/prd/PRD-SecurityAudit.md`.

Two things make now the right time regardless of packaging: `10.0.0-preview.41` is precisely the window for changing the public Actions surface — post-1.0 it becomes a breaking-change negotiation — and it must land **before** the planned Raven Skip/Take pushdown, or the security work gets done twice.

### Design

The paging problem **doesn't exist today** — `QueryExecutor` already materializes everything and pages in memory (`:169`, `:60-63`), and the in-memory `search` filter at `:46-58` already does the filter→count→page dance. So a post-filter drops in beside it with correct totals. That's the least-disruptive fix, not the correct one: it's permanently O(collection) and it *blocks* the pushdown.

The correct design is one read pipeline, two forms of row policy, fail-closed. Keep `IsAllowedAsync(string, T)` as the authoritative per-instance gate; add a composable `Expression<Func<T,bool>>? GetRowFilter(string action)` that **replaces the dead `OnQueryAsync`** and composes into the `IQueryable` before materialization. Detect which is overridden by reflection (cached), then pick per entity type. Only this gets all three properties:

| Strategy | Row security | Correct totals | DB-side paging |
|---|---|---|---|
| Today (queries + stream) | ❌ | ✅ | ❌ |
| Post-filter *after* Raven paging | ✅ | ❌ | ✅ |
| Post-filter *before* paging (today's PO path) | ✅ | ✅ | ❌ |
| **Composed predicate** | ✅ | ✅ | ✅ |

Caveat to design around: `Database.Cars` queries `VCar` via `Cars_Overview`, not `Car` (`Demo/Fleet/Fleet/Indexes/Cars_Overview.cs:30`). A predicate typed on `Car` can't compose unless the discriminating field is in the index — needs an automatic post-filter fallback plus a diagnostic, never silent non-filtering. Fleet's main query is projected, so this path is exercised on day one.

The stream fix is cheap: filter `batchList` at `StreamingQueryExecutor.cs:87` before breadcrumbs and mapping. Bonus — `StreamingDiffEngine` diffs against the previously emitted set, so a row that *becomes* invisible is emitted as a `remove` for free, which also partially closes the still-open **R2-M4**.

`totalItems` should become the post-filter count; pre-filter totals are themselves a minor disclosure.

**Also in scope:** record this finding in `docs/prd/PRD-SecurityAudit.md` under a properly-numbered heading, since the cited ID is fabricated and there is currently no written record of it anywhere.

---

## 6. Documentation fixes

All verified against source. Two extra issues found beyond the handoff's list.

| Doc | Wrong | Correct |
|---|---|---|
| webhooks README:226,234 | `CreateClientAsync` | `CreateInstallationClientAsync` (`IGitHubInstallationService.cs:9`) |
| authorization README:220,279-286 | `app.UseSparkAntiforgery();` | **No such method exists.** `UseSpark()` already wires antiforgery + the `XSRF-TOKEN` cookie (`SparkMiddleware.cs:153-155` says so explicitly). Delete the calls; rewrite the section. |
| authorization README (9 sites) | `AddSparkAuthorization` / `AddSparkAuthentication` / `MapSparkIdentityApi` as consumer API | All three are `internal`, visible only to `MintPlayer.Spark.Tests`. Real API is `spark.AddAuthorization(…)` / `spark.AddAuthentication<TUser>(…)` (`SparkBuilderExtensions.cs:13,26`), as `Demo/WebhooksDemo/.../Program.cs:29-30` actually does. `AddAuthentication<TUser>` registers the identity endpoints itself (`:38-39`), so `MapSparkIdentityApi` is never called by hand. **Doc bug, not an API bug.** |
| `GitHubWebhooksOptions.cs:30`, webhooks README:343,469 | "empty = allow all" | Code fails **closed** (`SparkBuilderExtensions.cs:101`), deliberately — a labelled `R2-H12` hardening against leaking private-repo webhook data. **Fix the doc**, not the code: "If empty, no dev connections are accepted." |
| webhooks README:347 | `ClientSecret` as a `GitHubWebhooksOptions` property | Absent from that class, correctly. It belongs to the OAuth login flow (`AddGitHub(…)`, already shown correctly at README:523). Delete the row, add a pointer. **Do not** add the property — it would be dead config blurring App-JWT and user-OAuth concerns. |
| webhooks README:256-263, 477-493 | `spark-github-{event}` queue-name tables | Fictional — no code ever produced them. Drop the queue-name column; the typed message is routed by CLR type. Keep `spark-github-all` (real). |
| README.md:20; authorization README:482 | "Angular 21" | Angular 22 |
| webhooks README:144,147; DevTunnel README:11 | `10.0.0-preview.22` / `.33` | `10.0.0-preview.41` |
| **(extra)** authorization README:471 | Cites `Demo/Fleet/Fleet/Program.cs` as showing all three methods | Fleet contains **none** of them; it uses `AddSparkFull`/`UseSparkFull`. Point at WebhooksDemo instead. |
| **(extra)** `docs/prd/PRD-GitHub-Webhooks.md:116,362` | References `GitHubQueueNames.FromEventType<TEvent>()` | Goes stale when the class is deleted (§1). |

### Additional broken API references found by sweeping the remaining READMEs

The handoff's list was the tip of it. A systematic sweep of every `libs/*/README.md` found a consistent failure mode: **READMEs document `IServiceCollection`-style method names that were later replaced by `ISparkBuilder` extensions**, plus several methods that never existed. Cron, Migrations, Testing and AllFeatures READMEs are clean.

**Messaging README** — `AddSparkMessaging()` (`:111`) and `CreateSparkMessagingIndexes()` (`:116`) are `internal`; the real API is `spark.AddMessaging()`, which wires the indexes automatically. `AddSparkRecipients()` (`:112`) should be `spark.AddRecipients()`. `AddRecipient<TMessage,TRecipient>()` (`:332`) **does not exist** — delete the row.

**SubscriptionWorker README** — the documented base constructor (`:43-44`, `:289-290`) won't compile: real signature is `(ILoggerFactory loggerFactory, IDocumentStore store)`. `TrackRetryAsync` returns `RetryOutcome`, not `bool`, so the `willRetry` samples (`:65-70`, `:270-273`) are wrong — use `retry.WillRetry`. `AddSparkSubscriptionWorkers()` (`:96-104`, `:360-381`) should be `spark.AddSubscriptionWorkers()` on `ISparkBuilder`.

**Spark core README** — `OnBeforeSaveAsync`/`OnAfterSaveAsync` samples (`:195`, `:202`) omit the `PersistentObject obj` first parameter and won't compile. `[LookupReferenceName]` (`:224`) **does not exist**. `AddSpark(IConfiguration)` (`:261`) is missing its required `Action<ISparkBuilder>`; `AddSpark(Action<SparkOptions>)` (`:262`) takes `ISparkBuilder`, not `SparkOptions`. `AddSparkActions()` (`:263`) should be `AddActions()`. `CreateSparkIndexes()` (`:270`) is private and automatic; `CreateSparkIndexesAsync()` (`:271`) **does not exist**.

Out of scope: historical PRD/plan docs correctly describing past states, and stock `ng new` boilerplate in demo ClientApp READMEs.

---

## 7. Non-goals

- **The Raven Skip/Take pushdown.** Becomes possible once `GetRowFilter` exists (§5), but it's a performance change with its own correctness surface and deserves its own PR and benchmarks. Explicitly *not* folded in.
- ~~**`MintPlayer.Spark.IdentityProvider`** stays on its unmerged branch~~ — **REVERSED.** The user directed that if an OIDC-provider branch existed it be folded into this PR so Coverage can reuse the `client_credentials` flow. It was ported (not merged — the merge base was 113 commits behind, spanning the `libs/` reorg and Angular 22) and now lives at `libs/identity_provider/`. It is in scope, and its security audit is **M12**, the largest single body of work in this PR. See [findings-identity-provider-audit.md](./findings-identity-provider-audit.md).

  The original reasoning still holds on one point: Coverage is an OIDC *client/verifier*, never a provider, so nothing in Coverage's own M0–M7 needs Spark to *issue* OIDC tokens. What changed is the upload credential — D1 makes `client_credentials` the CI-upload mechanism, which does need a provider.
- GitHub Actions OIDC JWT-bearer validation — Coverage's M7, not M0.
- Promoting `BsShellTopbarDirective` upstream (§4).
- Adding `ClientSecret` to `GitHubWebhooksOptions` (§6).
- Rewriting historical PRDs to match current state (§6).

### What the audit and its tests actually returned

Worth stating plainly, because it should change how the remaining milestones are planned.

**Two rounds of adversarial review** produced F1–F11 and O1–O27; a second round, covering the surface the first round's reviewer never reported on, produced N1–N10 including the only Critical. **The e2e suite then found four defects that every reviewer had read past:**

| Found by | Defect |
|---|---|
| e2e | **N5** — a property initializer re-added by the serializer on load, making every grant-type restriction unenforceable |
| e2e | **O25's fix was wrong** — `exact: true` against a lowercasing index inverted the matching, so the *correct* client id stopped resolving |
| a test guard | **N6** — authorize and issuance validated scopes against different sources, so a granted scope silently vanished from the token |
| a decision | **N1's fix broke standard introspection** — gating on ownership alone also refused the resource server RFC 7662 is written for |

**M12.7's route tests then found three more**, after the library half was already written, reviewed and unit-tested:

| Found by | Defect |
|---|---|
| writing a route test | **N12** — an Actions class refusal had no path to the caller at all: unhandled exception, 500, no body. Every validation message the audit phrased for an operator was unreachable. **Framework-wide** — `Demo/Fleet`'s `CarActions` throws the same way, so business validation has never reached a user anywhere in Spark |
| a route test needing a token | **N11** — every grant recorded the *requested* scopes on the token document while minting the JWT from the *granted* ones. Introspection reads the document, so it over-reported; disabling a scope narrowed the token and left introspection vouching for it |
| wiring the demo host | **N13** — `IOidcApplicationContext` declared its members `{ get; set; }`. An auto-property returns null and the query executor answers null with an empty result: screens that render and are always empty. The interface's own doc comment showed the broken shape, and the registration test had copied it |

In every case **the code read correctly at each individual point.** The defect lived in a round-trip, a seam between two components, or a case the tests hadn't thought to cover. Reading found the majority of the issues and could not have found these.

**And a fourth, from the withdrawal implementation itself:** after that feature was written, tested and pushed, a second adversarial pass found **N20** — the withdrawal sweep is best-effort, so a token it misses survives, and re-consenting used to bring it back for up to fourteen days. Six of this audit's findings are now defects in a *fix* rather than in original code (N11, N16–N18, N20, N21). The tally is the point: **the code most likely to be wrong is the code written to correct something else**, because it is written under the assumption that the thinking has already been done.

**And a fifth, from CI, which is a different kind entirely — N22.** Redirect-URI validation used `Uri.TryCreate(value, UriKind.Absolute, out _)` as an absoluteness test. It is not one, and the difference is **by operating system**: on Unix a bare `/callback` parses successfully and silently acquires the `file` scheme; on Windows it fails. The validation therefore rejected a relative redirect URI on a developer's machine and **accepted it on Linux**, where CI runs and where the app deploys. The tests agreed with the developer's machine and stayed green.

Nothing in the process could have caught this. It is not a seam between two components — it is a seam between two **platforms**, and every reviewer, every local run and every test shared the same wrong one. The first execution on the deployment platform was the earliest possible detection.

The general form is worth carrying further than this one bug: **an API whose contract was assumed rather than read.** `TryCreate` never promised what that code needed from it, and "it returns true for the values I tried" is not a specification. **Where a check is load-bearing, verify the API's actual contract, and run it on the platform you deploy to before trusting a green suite.** A WSL test environment is now committed (`testenvironments.json`) precisely so that is a thing someone can do locally.

**Sequencing consequence for M5 and M8–M11:** those milestones change authorization behaviour on paths that have no tests at all today. On this evidence, budget the test infrastructure *before* the fixes — the IdP work spent roughly a third of its effort on fixtures and got it back inside the first 24 tests.

**And a third, from M13 (consent withdrawal):** the four adversarial investigations run before that design was written found **three defects in N11's own fix**, shipped one commit earlier — no empty-scope floor, an announcement that never fired for the case it was written for, and a rotation that violated RFC 6749 §6 and turned a temporary scope disablement into a permanent one. N11 itself had been found by attacking N6's fix. **A fix is a change, and deserves the same adversarial treatment as the code it replaced.** Reviewing the diff is not the same as attacking the result: every one of these read correctly as a diff.

**And a second, sharper one, from M12.7:** unit tests against a class are not evidence that anything reaches it. Every one of N11–N13 sat behind a component that was individually correct and individually tested — the validation rules passed, the JWT was minted correctly, the interface compiled. What was missing was any test that traversed the seam: the route to the Actions class, the token to its record, the interface to a real host. **For each remaining milestone, name the seam before naming the fix.**

**Determinism is not optional.** Three separate flakes were fixed rather than tolerated. A flaky security test is worse than none: it teaches people to re-run until green, and the entire value of the suite is that red means something.

**And a sixth kind, from D14 — a proposed feature evaluated and declined.** Asked whether natural-id support should move to its own package behind a `spark.AddNaturalIds()` opt-in, three parallel investigations produced a conclusion the question had not anticipated: **the opt-in would have created the failure mode, not prevented one.** The convention is unconditional today, so no app can currently get it wrong; adding the call would mean implementing `IHasNaturalId` without it silently yields a GUID and every derived-id point-load returns `null` — indistinguishable from "not found". The general form: **when a feature's declaration is already its opt-in, a second switch is not configurability, it is a way to be half-configured.** Recorded because the instinct to make things opt-in is usually right, and this is the shape of the case where it is not.

The same investigation is where the practice of not trusting agent output paid: a draft of D14 asserted "no source generator in this repo emits diagnostics," which was **false** — `SPARK001`/`SPARK002` and five translation descriptors exist and ride on core as an analyzer reference. It was caught by asking for the claim to be verified before it shipped, not by review afterwards. The corrected fact strengthened the argument rather than weakening it, which is the usual outcome when a convenient claim turns out to be wrong.

**And a seventh, from writing documentation — the cheapest defect-finding exercise in this PR.** Asked to document which authentication schemes exist, what an unauthenticated caller gets, and what happens when authentication fails, three investigations returned two defects and a gap that no amount of reading the code had surfaced:

- **Nothing tested anonymous access to `/spark/po/*` at all.** Anonymous coverage existed only at the *introspection* layer — tests asserting what the server **reports** an anonymous caller may do. A permissions endpoint answering `CanCreate: false` while the create endpoint accepted the request would have satisfied every test in the suite. The gap was invisible because the tests that existed looked like they covered it.
- **`RowLevelAuthzTests` asserted absence against an eventually-consistent index with no wait** — the third instance of that exact shape in this PR, and the reason it now carries a positive control rather than a bare wait: a control also proves the row exists.
- **N23**, found only because two *new* tests disagreed with each other: creating a `Car` anonymously returns 401, creating a `Company` returns 400, because validation precedes authorization on the create path.

The general lesson: **writing down what a system does is a test of whether you know.** Every claim in the guide had to be either cited or pinned, and the ones that could be neither were the defects. Two more surfaced the same way — the Authorization README documented the fail-open behaviour R2-H1 had closed, and a missing `security.json` turns out to be an empty config rather than a locked door.

It also caught two of my own claims mid-flight: "no generator in this repo emits diagnostics" (false — `SPARK001`/`SPARK002` exist) and "no test anywhere exercises document-id generation" (too strong — the E2E suite does, via the real app). Both were corrected before shipping because the doc forced them to be checked.

**And what it found that nobody asked about — the unit/integration suite substitutes the thing it is testing.** All 47 `SparkTestDriver` fixtures take their store from `RavenTestDriver`, bypassing `AddSpark` entirely. The 23 that layer `SparkEndpointFactory` on top *do* call `AddSpark` — and then **remove the `IDocumentStore` it registered and substitute the same `RavenTestDriver` store** (`SparkEndpointFactory.cs:97-99`). So every fixture in `MintPlayer.Spark.Tests` runs on Raven's stock sequential ids while production runs on GUIDs.

An earlier draft of this paragraph said "no test anywhere" — **too strong, and corrected**. The E2E suite does exercise the real conventions, because `FleetTestHost.cs:274` shells out `dotnet run` against the actual Fleet project and gets its unmodified `Program.cs`. The divergence is specific to the unit/integration project, which is also where it is least visible.

This is the M8 lesson at suite scale: **the tests that most need attention are not the failing ones, they are the ones whose subject was quietly substituted.** Scoped as M14.2.

## 8. Verification

`npx nx run-many --target=test` — requires `RAVENDB_LICENSE` (JSON) or the root `raven-license.log`. No Docker; the "E2E" suite is embedded-Raven integration testing (`tests/MintPlayer.Spark.E2E.Tests/`, a real Fleet host over HTTPS on a random port), with Playwright available but mostly unused. Per repo convention, the full suite runs **once** at the end, not per milestone.

Per CLAUDE.md: never run `ng serve`/`npm start`/`ng build`/`ng test` against these workspaces — the ASP.NET hosts run the dev server themselves.

### How we re-check that a fix landed *everywhere*, not just where it was found

Nearly every finding in this PR was the same defect repeated across sites — five consent hops re-deriving the request, three token paths racing, three endpoints trusting a signature alone. Re-reading the code proves nothing about the *next* site someone adds, so the verification has to be mechanical:

The concrete case list is [idp-e2e-test-matrix.md](./idp-e2e-test-matrix.md).

1. **Behavioural E2E tests** for anything with observable behaviour — concurrent redemption of one code yields exactly one token set; a POST without an antiforgery token is rejected; a revoked token introspects as `active: false` and is refused by `/connect/userinfo`. These belong in `tests/MintPlayer.Spark.E2E.Tests/Security/`, beside the existing `ConcurrencyTests`, `XsrfCookieFlagTests` and `ReturnUrlValidationTests`.
2. **Coverage invariants** for anything that must hold across *all* endpoints, enumerated from `EndpointDataSource` rather than a hand-written list — so a route added later is included automatically and the test fails until it complies:
   - every interactive `/connect` POST carries `IAntiforgeryMetadata` with `RequiresValidation`;
   - the machine endpoints (`/token`, `/introspect`, `/revoke`) deliberately do **not** (asserting the exemption is intentional keeps someone from "fixing" it and breaking every OAuth client);
   - no registered RavenDB index is queried on an authorization decision — the derived-id rule from findings §3.
3. **A final audit sweep before merge**, covering both the remaining open findings and the surface that was never reviewed (signing keys, JWKS, introspection/revocation caller-auth). The IdP arrived as unreviewed third-party-shaped code; a second pass over what changed since is the point at which "audited and proven sound" (D1) is actually earned.

Item 2 is the one that answers "is it implemented everywhere?" durably. Items 1 and 3 answer "does it work?" and "what did we miss?".

## 9. Release mechanics

- **Every push to `master` runs the publish pipeline** (`dotnet-build-master.yml`), with `--skip-duplicate` — an unbumped version is a silent no-op, not a failure. Never publish by hand from a feature branch.
- Versions are **hand-maintained across 20 csprojs**; there is no bump script.
- A new package (§2) **must be added to `MintPlayer.Spark.sln`**, or CI's bare `dotnet restore` never restores it and the `--no-restore` build fails. `dotnet pack`/`push` are glob-based, so no workflow edit is needed. No `project.json` is required for .NET libraries — `@nx/dotnet` infers targets.
