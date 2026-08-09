# PRD — Multi-host end-to-end testing

**Status:** Draft for review
**Date:** 2026-08-09
**Branch:** `feat/spark-hardening-m0` (PR #231) — spec only; no implementation in this PR
**Origin:** Two gaps left open by the Coverage-handoff work ([PRD-CoverageHandoff.md](./PRD-CoverageHandoff.md)),
plus a live defect (**F15**) found while scoping this one.

---

## 0. Summary

Spark is a framework for applications that talk to each other — modules replicate data, one app
issues tokens another accepts. **Every test we have runs one host.** Where a second participant is
required, the tests fabricate its half: a test-authored `HttpClient` posts the request a consumer
module would have sent.

That is not a hypothetical weakness. Three defects in the last two days lived precisely in the space
a single host cannot see:

| Finding | What it was | Why one host missed it |
|---|---|---|
| **F13** | The replication endpoints validated a caller and never authenticated it, so every cross-module write would have been refused in production | The endpoint's own tests supplied the request; nothing ran the caller's code path |
| **F14** | A configured `SparkModulesUrls` was appended after the hardcoded default, so every deployment talked to `localhost:8080` | One host never has to *find* another |
| **F15** | HR granted `Replicate/People` but not `Replicate/Companies`; because scripts are batched per module, Fleet's real deployment would have been refused **in full** | The tests hand-build the request and choose the collections, so they never batch two with one grant missing |

All three were found by reasoning, not by testing. That is the thing to fix.

**This PRD covers two scenarios and one enabler:**

| # | Scenario | Nature |
|---|---|---|
| — | **Multi-host harness** | Test infrastructure. Small, because of §1. |
| 1 | **Replication**: host1 collects its ETL scripts and sends them to host2, which deploys them | Test-only — the product code exists |
| 2 | **Cross-app sign-in**: a user signs in to host2 through host1's IdentityProvider | **Feature work first.** The consumer half does not exist |

Scenario 2 is not a test task. Saying so up front is the point of this document.

---

## 1. The enabler is cheaper than expected

`SparkTestDriver`'s static constructor calls RavenDB's **global** `ConfigureServer`, not
`ConfigureScopedServer` (`libs/testing/MintPlayer.Spark.Testing/SparkTestDriver.cs:29`). RavenTestDriver's
global mode runs **one embedded server per test process**; `GetDocumentStore()` creates a fresh
database on it per call.

So two hosts already share a RavenDB server. Making them share a **`SparkModules` registry** — the
directory through which modules discover each other — is passing the same database name, not building
cross-host discovery. `FleetTestHost` already derives that name from a per-host suffix
(`TestModulesDatabase`, `:49`); it needs to become an input.

The rest of the harness work is mechanical, and most of it is already done. `FleetTestHost` was
generalised twice this week for the credential tests: the ASP.NET environment name and the certificate
mode are parameters, the `appsettings.{Env}.json` override and the OIDC signing key are per-host files,
ports are dynamic, and the build is serialised behind a static gate after two concurrent `dotnet run`s
raced on Fleet's output DLL.

What remains:

- **The app is hardcoded to Fleet** in four places — project path, working directory, ClientApp path,
  dist path. It needs to name a demo instead.
- **The build gate builds only Fleet** (`_fleetBuilt` is a single flag). It needs to be per-project.
- **The Angular bundle step is Fleet-specific** and is pure cost for a host whose UI is not exercised.
- **Databases are per-host by construction.** The app database should stay that way; the modules
  registry must be shareable.

**Estimate: ~150 LOC of change to `FleetTestHost`, plus a fixture per topology.** No new mechanism.

---

## 2. Scenario 1 — replication, and an honest ceiling

### What a two-host test adds

Today's E2E tests POST a hand-built `EtlScriptRequest` at `/spark/etl/deploy`. That covers the
endpoint's authorization and plumbing well — it is where F12 and F13 were caught — and covers **none**
of the consumer's outbound path:

1. A real module's startup scanning `[Replicated]` and grouping scripts per source module
   (`EtlScriptCollector`).
2. The durable message bus actually carrying `EtlScriptDeploymentMessage` — queueing, subscription
   worker pickup, backoff, dead-lettering.
3. `EtlScriptDeploymentRecipient` resolving the peer's URL from a **real** shared registry and POSTing
   to a **real** second host (today: a stubbed `HttpMessageHandler`).
4. Two hosts registering each other and the timing that implies.

F15 sits squarely in step 1 — the batching — which is why no existing test could see it.

### The ceiling is a licence setting, not a law — **measured, 2026-08-09**

> **Correction.** An earlier draft of this section said the ceiling was absolute. It is not. Running the
> same assertion under two licences settles it:
>
> | Licence | `/spark/etl/deploy` result |
> |---|---|
> | `raven-license.log` (the repo default) | **500** — `LicenseLimitException` |
> | `dev-license.tmp` (a RavenDB **developer** licence) | **200 OK** — the ETL task deploys |
>
> So with the developer licence, a test **can** assert real ETL deployment, and §2's whole
> assert-by-elimination apparatus becomes unnecessary. The residue named below shrinks to nothing.
>
> **✅ Resolved the same day.** The `RAVENDB_LICENSE` organisation secret now holds the developer
> licence, so CI has the ETL feature. `Etl_deployment_is_accepted_for_a_granted_collection` was
> strengthened accordingly: it asserts **200** and that the ongoing ETL task `spark-etl-HR` exists on
> the owner, instead of asserting the absence of a refusal.
>
> **Design the test to say which world it is in.** If the licence lacks ETL, the strict assertion should
> **skip with a stated reason**, not quietly weaken to something that passes anyway. A test that silently
> lowers its own bar is indistinguishable from a passing one, which is the failure mode this document
> keeps returning to.

### The workaround below is now history, kept for the reasoning

Everything from here to the end of §2 was designed around a licence that could not deploy ETL. With
the developer licence in place it is **no longer needed** — R2 can assert the ETL task directly. It is
retained because the reasoning is reusable: it is what to do when a test can observe only the absence
of the wrong failure, and because the distinction it draws (authorization refusal is visible; internal
failure is deliberately opaque) remains true of the endpoint regardless of licence.

**Under a licence without the ETL feature, a test cannot assert that an ETL task was created.** The embedded server *is* licensed —
`SparkTestDriver` loads `raven-license.log` before any test runs — and `AddEtlOperation` still throws
`LicenseLimitException`, because that licence tier excludes the ETL feature. The existing single-host
test already documents this and asserts "got past authorization" instead of success.

Two things raise the ceiling above today's:

- **`PutConnectionStringOperation` runs before the licence-gated call** and is not feature-gated. A
  test can assert `spark-etl-{module}` exists on the target via `GetConnectionStringsOperation` —
  licence-independent proof the deployment reached RavenDB, which nothing asserts today.
- **Assert by elimination on the failure**: require the terminal error to be the licence limitation
  and *not* `Forbidden` or a connection error. F15 is exactly the case that distinction catches.

### What remains assumed, named precisely

The chain splits into three, and only the middle one is what a two-host test adds:

| Link | Provable? | How |
|---|---|---|
| The consumer collected the right scripts | **Yes** | The queued `EtlScriptDeploymentMessage` is a real document — assert its contents against the `[Replicated]` declarations |
| Transport, authentication, authorization, arrival at RavenDB | **Yes** | Owner-side connection string, plus the elimination assertions below |
| Script text → `RavenEtlConfiguration` → RavenDB accepts it → data moves | **No** | The translation lands only in the licence-gated `AddEtlOperation` call |

So the honest claim is: *the consumer sent the right scripts* and *the owner accepted a deployment and
reached RavenDB* — **not** *the owner received these scripts*. The owner's copy of the script text is
not observable without the ETL feature.

**Do not let that gap stand as "implicitly working".** Between "authorized" and "data replicates" sits
`EtlTaskManager`'s translation — transformation names, collection lists, the connection-string
reference, the task name. A bug there is invisible to every assertion above.

It is closable without a licence. `DeployAsync` takes an `IDocumentStore` and `EtlTaskManagerTests`
already mocks one; capturing the `AddEtlOperation` argument and asserting the exact configuration —
one transformation per script, correct collections, correct script text, correct connection-string
name — pins the translation at unit level. **Add that alongside the two-host test, not instead of it.**

That reduces the residue to one thing: *RavenDB accepts a well-formed ETL configuration and moves
data*. That is RavenDB's behaviour, not Spark's, and it is a reasonable thing to take on trust.

> **Worth checking before building any of this.** The repo licence (`raven-license.log`, `Name: "2sky"`)
> lacks the ETL feature. If the organisation holds a tier that includes it, pointing CI at that licence
> collapses this entire section into a direct end-to-end assertion and makes the layering unnecessary.

**Resolved (see the plan's R1).** The consumer *can* tell authorization apart from everything else — a
`403` carries a body containing `Forbidden`, which lands verbatim in the message's `LastError`. It
*cannot* tell a licence failure from any other internal failure: every `EtlTaskManager` error returns a
bare `500` with no body, deliberately (R2-L6). The true cause is provable only on the **owner**, in its
process log, which the harness already captures.

So the test makes two independent claims — the consumer's message dead-lettered *without* `Forbidden`,
and the owner's log carries the licence exception — rather than one weak claim that "the message
failed", which would pass whenever replication is broken for any reason at all.

One more thing this exposed: **the message bus's retry policy cannot be configured.** `AddMessaging`
takes a C# delegate and binds no configuration section, so `MaxAttempts` and the backoff schedule are
compile-time constants. That blocks the test's determinism (defaults would make it minutes long) and is
a real gap for operators, who cannot tune a durable bus's retry behaviour from `appsettings` either.

### Determinism

The chain is asynchronous — a detached startup `Task.Run`, then a polling subscription worker — so the
test must poll a terminal state, never assume delivery. The retry schedule must be shortened from
configuration for the test, or a licence-blocked deploy takes minutes to dead-letter.

---

## 3. Scenario 2 — cross-app sign-in needs a feature first

### What does not exist

There is no way for a Spark app to delegate user sign-in to another Spark app. `AddOidcLogin`,
`SPARK_OIDC_PROVIDERS` and `provideSparkOidcLogin` appear only in planning documents. What exists is a
**closed set** of social providers (`ExternalLoginOptions`: Google/Microsoft/Facebook/Twitter/GitHub)
and a hand-rolled `AddGitHub` wrapping raw `AddOAuth`. Fleet consumes HR's provider only for
`client_credentials` — machine-to-machine, no user.

### What the provider side is ready for

Nothing to fix. Spark's discovery document emits every field the stock
`Microsoft.AspNetCore.Authentication.OpenIdConnect` handler requires, and its JWKS parses. The package
is not referenced anywhere yet and must be added explicitly — the framework reference does not include
auth handlers, which is why `JwtBearer` is already an explicit `PackageReference`.

Two consumer-side settings are non-default and load-bearing: `ResponseType = Code` (the handler
defaults to hybrid; Spark accepts only `code`) and, if PKCE is enabled, S256 (Spark rejects `plain`).

### Three traps worth designing against

1. **`SignInScheme = IdentityConstants.ExternalScheme`.** Without it the ticket never lands where
   `GetExternalLoginInfoAsync()` looks, and the callback sees nothing.
2. **Email claim mapping.** Spark's id_token emits a bare `email` claim; the handler does not map it
   to `ClaimTypes.Email`, which is what the callback reads. Unmapped, **every first-time sign-in is
   refused as `email_not_verified`** — a failure that looks like a policy decision and is a mapping bug.
3. **`CallbackPath` must not default.** Two OIDC providers both on `/signin-oidc` shadow each other,
   last registration winning. This is the same shape as the `Audience` trap: looks configured, is
   broken. Derive it from the scheme name and validate.

There is also a **configuration footgun** that is not a code bug: Spark emits `email_verified` only if
the granted scope's `OidcScope.ClaimTypes` includes it. A demo whose `email` scope omits it refuses
every first-time cross-app sign-in, for a genuinely confirmed account.

### The finding that shapes the scope

**Group claims do not propagate.** `ClaimsGroupMembershipProvider` reads groups off the *local*
principal, which `SignInManager.SignInAsync` builds from the *local* user's stored claims. The external
principal's claims are read only for provisioning (name, email) and are never copied.

So a user signing in to Fleet via HR gets a **new Fleet-local account with no groups**, whatever they
hold on HR. Cross-app SSO through this seam is identity-linking, not authorization propagation.

That is a legitimate design — it keeps each app's authorization its own — but it must be **stated, not
discovered**. Propagating groups is a separate problem (a scope whose granted claims are written onto
the local user at provisioning) with its own security surface: it lets the issuer grant authority in
every app that trusts it. **Non-goal here.**

### Shape and cost

`AddOidcLogin` belongs on `IdentityBuilder` beside `AddGitHub`, not on `ISparkBuilder` beside
`AddJwtBearerCredential` — an interactive login ends as an ordinary application cookie, which is
already an ambient credential scheme. Fail fast on a missing `ClientId`.

**~500–1000 LOC across 6–9 files**: framework extension, demo wiring (including seeding an
`OidcApplication` for Fleet on HR), harness work shared with scenario 1, and tests. Multi-day.

### Drive it with HttpClient, not Playwright

Every `/connect/*` page is server-rendered HTML with no client JS. `HttpClient` with
`AllowAutoRedirect = false` is not merely sufficient, it is **better**: the test can assert each
redirect hop — the `Location`, the `state` echo, the error parameters — instead of only where the
browser landed. The in-process `OidcTestHost.Browser` (a cookie jar plus antiforgery scraping) is the
pattern to transplant onto two live hosts. A Playwright smoke test for the "Sign in with HR" button is
a thin, separate addition, not a reason to drive the protocol through a browser.

---

## 3a. Decision — a dedicated `Demo/SparkId` app hosts the provider

Three apps could play issuer: HR (which does today), `DemoApp`, or a new one. **A new
`Demo/SparkId/` — with no Angular ClientApp.**

The original IdentityProvider PRD already specified `Demo/SparkId/`; the M12.1 port brought the
library across and left the demo behind, which is how HR ended up doubling as an identity provider and
Fleet ended up self-issuing. Neither is the shape anyone deploys.

The reason that is more than aesthetic: **an identity provider needs no SPA.** Every `/connect/*` page
is server-rendered. HR and `DemoApp` both carry an Angular ClientApp, and the harness's one serial
cost is exactly that — `BuildGate` deliberately serialises builds, so a cold Angular production build
lands on the front of the critical path. A provider with no ClientApp adds a `dotnet build` and a
process start, and nothing else.

It also lets Fleet stop self-issuing. Fleet hosts a provider today only because there was no second
host to issue from; once there is, that wiring becomes a demo of something nobody does.

**Costs:** one csproj, one solution entry, one more project in CI's build. Demos are not published, so
no version bump. Against that, using `DemoApp` would cost an Angular build on the critical path and
keep the conceptual muddle of a business app moonlighting as an identity provider.

**Consequence for scenario 2:** SparkId issues, Fleet consumes — which is also the topology Coverage
will deploy, so the relying-party extension gets exercised the way it will actually be used.

## 4. Non-goals

- **Proving data actually replicates.** Licence-bound; see §2.
- **Propagating authorization across apps.** §3. Identity-linking only.
- **A browser-driven OIDC protocol test.** §3.
- **Three or more hosts.** Two covers both scenarios; the harness should not be shaped around a third.
- **Changing the all-or-nothing ETL refusal.** Recorded as F15's open design question. A partial deploy
  that silently skips collections is worse than a loud refusal; if it bites, the fix is a better error
  body naming the refused collections.

---

## 5. Sequencing, and why scenario 1 goes first

Scenario 1 is test-only and its harness work is shared. Scenario 2 needs a framework feature, demo
wiring and seeded OIDC registrations before a single assertion can be written.

Doing scenario 1 first also puts the harness under load early — two real hosts, a shared registry, an
asynchronous chain — which is where the surprises will be. Discovering them while the second scenario
is still a document is much cheaper than discovering them halfway through a feature.

**Recommended: build the harness and scenario 1 together; treat scenario 2 as a separate milestone
that starts with the extension, not with a test.**
