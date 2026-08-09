# Implementation plan — Multi-host end-to-end testing

See [PRD-MultiHostE2E.md](./PRD-MultiHostE2E.md). Findings that motivated it: **F13**, **F14**, **F15**
in [findings-replication-mtls.md](./findings-replication-mtls.md).

**Status: not started.** This document ships in PR #231 alongside the work that uncovered the gaps; the
implementation is a separate piece of work.

TDD where there is behaviour to pin. Per CLAUDE.md, test suites run once at the end of a milestone
sweep, not after each step.

## Status — resume here

| Milestone | What | Size | State |
|---|---|---|---|
| **H1** | Generalise `FleetTestHost` to run any demo app | Small | Not started |
| **H2** | Shareable `SparkModules` registry + a two-host fixture | Small | Not started |
| **R1** | Can a consumer distinguish a licence failure from a security failure? | — | ✅ **Resolved** — partly; see below |
| **R1a** | Bind `Spark:Messaging:*` from configuration (blocks R2's determinism) | Small | Not started |
| **R1b** | Decide the CI licence | — | ✅ **Resolved** — secret now holds a licence with ETL |
| **R2** | Scenario 1 — real consumer → real owner ETL deployment | Medium | Not started |
| **S1** | New `Demo/SparkId` app — the provider, no ClientApp | Small | Not started |
| **R3** | SparkId issues, Fleet validates — the real token topology | Small | Not started (product-side unblocked) |
| **O1** | `AddOidcLogin` — the OIDC relying-party extension | Medium | Not started |
| **O2** | Demo wiring: Fleet signs users in through HR | Small | Not started |
| **O3** | Scenario 2 — cross-app sign-in, end to end | Medium | Not started |

**Ordering that is not negotiable:** H1 → H2 → R2, and O1 → O2 → O3. R1 gates R2's assertions but not
its harness work. Scenario 2 must not start with a test.

---

## H1 — Generalise the host

`FleetTestHost` is already parameterised on the things that collide (environment name, certificate
mode, per-host settings and signing-key files, dynamic ports, a static build gate). What remains is
that the *app* is Fleet in four places: the project path, the working directory, the ClientApp path and
the dist path.

- Introduce a small descriptor — demo directory, project file, whether an Angular bundle is needed —
  and take it as an `init` property defaulting to Fleet, so every existing fixture keeps working
  unchanged.
- **Three config *values* are Fleet-shaped too**, not just paths: the app-database prefix
  (`SparkFleetE2E-`), the `Spark:Replication:ModuleName` written into the override (`"Fleet"`), and the
  `Spark:JwtBearer` section (audience `"fleet-api"`). The JWT section should be emitted only for a host
  that actually validates tokens, rather than always.
- **Make the build gate per project.** It is currently one static `_fleetBuilt` flag; with two
  different apps it would build the first and run the second `--no-build` against nothing. Key it by
  project path.
- **Skip the Angular bundle for hosts whose UI is not exercised.** It is pure wall-clock for an API-only
  participant, and `EnsureAngularBundleAsync` shells out to `npm run build` when `dist/` is empty.
- Rename to something honest (`SparkDemoTestHost`) or keep `FleetTestHost` and accept that the name
  lies. Prefer renaming: three fixtures reference it and the compiler finds them all.

**Do not** generalise the seeding helpers speculatively. `SeedUserAsync` and friends are Fleet-shaped
because Fleet is what they seed; a second app that needs seeding can grow its own.

## H2 — A shared registry, and a two-host fixture

The enabling fact (verified): `SparkTestDriver` calls RavenDB's **global** `ConfigureServer`
(`SparkTestDriver.cs:29`), so every host in a test process shares one embedded server. Two hosts
therefore share a registry by sharing a database *name*.

- Make `TestModulesDatabase` an input rather than a derivation of the per-host suffix. Default stays
  per-host, so existing fixtures remain isolated.
- Keep the **app** database per-host. Two modules sharing an app database would be a different system.
- Add a fixture that starts Fleet and HR against one registry, each with its own app database, and
  waits for both to be ready.
- **Assert the registry is actually shared during fixture setup** — both `moduleInformations/Fleet` and
  `moduleInformations/HR` present. F14 was exactly a case of two processes silently disagreeing about
  which server they were talking to; a fixture that proves the precondition turns that class of bug
  into a setup failure instead of a mysterious refusal downstream.

**Cost check — and a correction.** An earlier draft of this plan said a second app "adds a startup, not
a build". That is wrong: a *different* app must be built, and `BuildGate` is a single semaphore that
serialises builds deliberately (two concurrent `dotnet run`s once raced on Fleet's output DLL). So HR's
`dotnet build` — plus, on a cold CI machine, its Angular production build — lands **serially on the
front of the critical path**. A cold Angular build is commonly tens of seconds. Measure it in CI rather
than estimating; if it hurts, skip HR's bundle (H1) since its UI is not exercised. If the suite climbs
past roughly 2 minutes, split the multi-host collection into its own CI step rather than making the
tests shallower.

## R1 — Can the consumer tell *why* it was refused? — ✅ **resolved**

**Partly yes, and the boundary is by design.**

- **Authorization refusal is distinguishable.** `EtlDeploy` returns `403` with a real JSON body
  containing `"Forbidden"`; the recipient puts the response body verbatim into
  `SparkMessage.Handlers[0].LastError`. Consumer-side, deterministic, licence-independent. This is the
  F15 shape, and the existing single-host test already leans on it.
- **Internal failures are not distinguishable from each other.** Every `EtlTaskManager` failure —
  licence limit, self-loop, any future bug — returns a bare `500` with **no body**. The descriptive
  error it builds is thrown away rather than serialised, deliberately (R2-L6, to avoid leaking
  connection-string detail to a caller). So the consumer sees `InternalServerError` and an empty
  string, and *cannot* prove the licence was the cause — only that it was not authorization.
- **The real cause is observable on the owner**, in its process log: `EtlTaskManager` logs the actual
  exception, and `FleetTestHost` already captures host stdout (`RecentLog`).

**Assertion strategy, therefore — two independent claims, neither of them "the message failed":**

1. *Consumer-side:* the message reaches `DeadLettered` and its `LastError` does **not** contain
   `Forbidden` — proving it got past authorization.
2. *Owner-side:* the captured log contains the licence-limit exception — the only place the true cause
   is provable rather than inferred.

Do not merge these into one assertion, and do not claim the test proves an ETL task would have been
created. It proves the live pipeline reached the owner and was refused **only** by the licence.

### R1a — messaging retry policy is not configurable (blocks R2's determinism)

Making a failed deploy settle in seconds needs `MaxAttempts = 1`: it is captured onto the message at
broadcast time, and the worker dead-letters on the first attempt without scheduling any backoff (already
pinned by `MessageSubscriptionWorkerE2ETests`). Defaults are `MaxAttempts = 5` with backoff
`[5s, 30s, 2m, 10m, 1h]` — minutes per test.

**But there is no configuration key for it.** `AddReplication` binds `Spark:Replication` from
`IConfiguration`; `AddMessaging` takes only a C# delegate baked into `Program.cs`. No `Spark:Messaging:*`
key exists anywhere in the repo, so the harness — which configures hosts exclusively through a generated
`appsettings.{Environment}.json` — cannot reach it.

**Decision: add the binding**, mirroring `AddReplication`'s existing pattern. It is a small, precedented
diff, and it is not test-only plumbing: **an operator cannot tune retry or backoff policy from
configuration today either**, which is a real gap in a durable message bus. The alternative — running
this scenario in-process via `SparkEndpointFactory` to inject options directly — trades away exactly the
realism (a real `dotnet run`, real Kestrel, real certificate handshake) that the subprocess harness
exists to provide.

## R1b — The licence — ✅ **resolved**

Measured: the repo's `raven-license.log` refuses ETL (500); a RavenDB **developer** licence deploys it
(200). Same test, same code, only the licence differed. The `RAVENDB_LICENSE` organisation secret now
holds the developer licence, **so CI has the ETL feature**.

Consequences, already applied to the existing single-host test and carried into R2:

- `Etl_deployment_is_accepted_for_a_granted_collection` asserts **200** and that the ongoing ETL task
  exists, rather than asserting the absence of a refusal.
- R2 does **not** need the connection-string proxy, the `LastError` elimination, or owner-log scraping.
  Assert the ETL task directly.
- **Local runs still need the licence.** `LicenseHelper` reads `RAVENDB_LICENSE` first, then repo-root
  `raven-license.log`. A checkout whose `raven-license.log` predates the change will fail this test —
  update that file, or set the environment variable.

## R2 — Scenario 1

**Shape:** HR (consumer) boots naturally so its real `UseSparkReplication` startup task runs; Fleet
(owner) receives the deployment its subscription worker sends.

Use the **HR → Fleet** direction. Fleet grants `Module:HR` both `Replicate/Cars` and `ReadEditNew/Car`,
so it is complete. The Fleet → HR direction is the one F15 just repaired, which makes it the better
*regression* test — add it second, once the direction that should work does.

**Make it deterministic before making it thorough:**

- Set `MaxAttempts = 1` via R1a's new configuration key, so a failed deploy dead-letters on the first
  attempt with no backoff scheduled.
- **The polling pattern that avoids the staleness trap:** the test does not know the message id (the
  consumer's own startup broadcasts it), so query `SparkMessages` for
  `QueueName == 'spark-etl-deployment'` in a 100 ms loop until it appears, then **switch to
  point-loading by id** for the rest of the wait. The loop self-heals past the query's staleness window,
  so no `WaitForIndexing` is needed and no assertion is ever made against a stale index — the trap this
  branch has hit three times.
- Wait for `DeadLettered`, not `Completed`: under the licence limitation the deploy fails, so a test
  waiting for success would wait forever.

**Assertions, strongest first:**

1. The **owner's** store has the ongoing ETL task `spark-etl-{RequestingModule}` — note the name comes
   from the *requesting* module, so HR pulling from Fleet creates `spark-etl-HR` on Fleet. This is the
   direct proof, available now that CI's licence includes ETL (R1b).
2. The consumer's message reached `Completed`, not `DeadLettered` — the whole pipeline, end to end.
3. R1's elimination assertions are **no longer needed** for the happy path. Keep them in mind only for
   the negative cases, where distinguishing "refused by authorization" from "failed internally" still
   matters.

**Pin the translation at unit level in the same milestone.** The two-host test cannot observe the
owner's copy of the script text — it lands only in the licence-gated `AddEtlOperation` call — so
without this, everything between "authorized" and "data replicates" is untested. `DeployAsync` takes an
`IDocumentStore` and `EtlTaskManagerTests` already mocks one: capture the `AddEtlOperation` argument and
assert the exact `RavenEtlConfiguration` — one transformation per script, correct source collections,
correct script text, correct connection-string name and task name. Cheap, licence-free, and it reduces
what is taken on trust to "RavenDB accepts a well-formed configuration", which is RavenDB's behaviour
rather than Spark's.

**Then the negative**, which is the one with teeth: revoke one `Replicate/{Collection}` grant and assert
the whole batch is refused — pinning F15's behaviour deliberately rather than leaving it as a demo
config that happens to be right.

## S1 — `Demo/SparkId`

A minimal Spark app whose only job is to be the identity provider: `AddIdentityProvider`, a
`SparkContext` for the OIDC PersistentObjects (copy HR's, which M12.7 already built), `security.json`,
and **no Angular ClientApp**. The `/connect/*` pages are server-rendered, so there is nothing for an
SPA to do — and skipping it keeps the harness's one serial cost (a cold Angular build behind the build
gate) off the critical path.

Model it on HR's Program.cs minus the business wiring. Pin the issuer from configuration; it is
required outside Development (O7).

Then **retire Fleet's self-issuing** in the same milestone: it exists only because there was no second
host, and leaving it would leave two ways to configure the same thing. Fleet keeps
`Spark:JwtBearer:Authority` (already decoupled) and drops `AddIdentityProvider`. Keep HR's provider or
retire it too — that is a demo-story decision, not a technical one.

## R3 — SparkId issues, Fleet validates (the real token topology)

Scenario 1's sibling, and cheap now. Fleet's demo wiring bound `jwt.Authority` to the issuer it
self-hosts, so it could only trust a provider it was also *being* — which made "HR issues, Fleet
validates" impossible to configure. **Already fixed**: `Spark:JwtBearer:Authority` is now independent
and falls back to the self-hosted issuer, so the single-app demo is unchanged.

With a two-host fixture, point Fleet's authority at SparkId's issuer, register an `OidcApplication` on
SparkId with an audience Fleet accepts, and assert a token minted there authorizes a write on Fleet.
This is the topology Coverage will actually deploy, and it needs no new product code — only the
fixture and S1.

Worth doing **before** the OIDC milestones: it is the same participants and the same harness, without
needing a relying party to exist.

## O1 — `AddOidcLogin`

**Start here, not with a test.** The consumer half of cross-app sign-in does not exist.

- New `PackageReference` for `Microsoft.AspNetCore.Authentication.OpenIdConnect` in
  `MintPlayer.Spark.Authorization` — auth handlers are not in the framework reference, which is why
  `JwtBearer` is already explicit there.
- An `IdentityBuilder` extension beside `AddGitHub`, **not** an `ISparkBuilder` credential scheme: an
  interactive login ends as an ordinary application cookie, which is already registered ambient.
- Wrap `AddOpenIdConnect` with the settings that are load-bearing and non-default:
  `ResponseType = Code`; `SignInScheme = IdentityConstants.ExternalScheme`; an explicit `CallbackPath`
  derived from the scheme name; `ClaimActions` mapping the bare `email` claim onto `ClaimTypes.Email`.
- **Fail fast on a missing `ClientId`**, the way `AddJwtBearerCredential` refuses an empty `Audience`.
  Same reasoning: the failure otherwise appears far from its cause, deep inside the handler on first
  challenge.
- **Do not let `CallbackPath` default.** Two providers on `/signin-oidc` shadow each other silently.

Each of those four settings has a specific, non-obvious failure mode. Write the interface comment
explaining what breaks without it — that is the difference between a wrapper and an abstraction.

Unit-test the registration guards (missing `ClientId`, colliding callback paths) the way M10's
extensions are tested. They are cheap and they are what a consumer hits first.

## O2 — Demo wiring

- Fleet registers `AddOidcLogin("HR", …)` pointed at HR's issuer.
- HR needs a registered `OidcApplication` for Fleet: `authorization_code`, Fleet's exact callback URI,
  and allowed scopes with matching enabled `OidcScope` documents.
- **The `email` scope must declare `email_verified` in its `ClaimTypes`**, or every first-time sign-in
  is refused as `email_not_verified` — a data footgun that reads exactly like a policy decision. Seed it
  correctly and comment why.
- A "Sign in with HR" trigger in Fleet's shell reusing the existing `loginWithProvider()`.

## O3 — Scenario 2

Drive it with `HttpClient` and `AllowAutoRedirect = false`, transplanting the `OidcTestHost.Browser`
pattern (cookie jar plus antiforgery scraping) onto two live hosts. No Playwright: the `/connect/*` pages
are server-rendered with no client JS, and stopping at each redirect is what lets the test assert the
`Location`, the `state` echo and the error parameters rather than just where it landed.

**Cases:**

1. The happy path — Fleet challenges, HR authenticates and consents, the code comes back, Fleet
   exchanges it and establishes a session. Assert Fleet's `/spark/auth/me` reports the user.
2. **Groups do not propagate** — a characterization test. The user holds a group on HR and lands on
   Fleet with none. This is intended behaviour and must be pinned deliberately, so that if it ever
   changes, it changes on purpose.
3. `redirect_uri` mismatch is refused — exact ordinal matching, no trailing-slash tolerance.
4. *(If cheap)* the `email_verified` footgun: a scope not declaring it refuses a first-time sign-in.

Case 2 is the one to write first. It is the finding most likely to surprise whoever wires this up in
anger, and a test is a better place to learn it than production.

---

## Risks

- **The harness gets slower than the value it adds.** Two hosts per topology, several topologies. Watch
  wall-clock; split the CI step before making tests shallower.
- **Scenario 1's ceiling disappoints.** It cannot prove data replicates. If that is the property
  someone actually wants, the answer is a RavenDB licence with ETL, not a cleverer test — and it is
  better to know that now than to build the harness expecting more. **Check first whether the
  organisation's licence includes the ETL feature**; the repo's does not, and if another does, most of
  R2's assertion gymnastics becomes unnecessary.
- **Scenario 2 grows.** An OIDC relying party is a real feature with a real security surface. If it
  starts sprawling, ship O1 with unit-tested guards and let O3 follow separately; a half-built RP behind
  a passing E2E test is worse than no RP.
- **Two hosts, one repo directory.** Both write settings and key files into their project directories
  and delete them on dispose. Already parameterised per environment name, but any new per-host file has
  the same trap — the last dispose wins.
