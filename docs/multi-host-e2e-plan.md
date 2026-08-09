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
| **R1** | Resolve the open question: can a consumer distinguish a licence failure from a security failure? | Tiny, **blocking R2** | Not started |
| **R2** | Scenario 1 — real consumer → real owner ETL deployment | Medium | Not started |
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

**Cost check.** The suite runs 77 tests in ~44s in CI with two Fleet hosts. A third host adds a startup,
not a build (gated). If the total climbs past roughly 2 minutes, split the two-host collection into its
own CI step rather than making the tests shallower.

## R1 — Can the consumer tell *why* it was refused? (blocking)

`EtlTaskManager` catches the real exception and returns `ETL_DEPLOY_FAILED`; `EtlDeploy` returns a bare
500; the recipient throws `HttpRequestException` and the message eventually dead-letters.

**Establish, before writing any assertion, whether the terminal error distinguishes:**

- refused by authorization (`403 Forbidden` — the F15 shape),
- failed at RavenDB for the licence (the expected outcome in tests),
- failed to connect at all (the F14 shape).

If it does not distinguish them, **say so in the plan and change the assertion strategy** — do not write
a test that asserts "the message failed". That passes when replication is broken for any reason, which
is the exact failure mode this work exists to eliminate. The fallback is owner-side observation: assert
the connection string exists (proving the deploy reached RavenDB past authorization) and assert the
owner logged no authorization refusal.

If it does not distinguish them and that seems wrong, note it as a finding — a consumer that cannot tell
"you may not replicate this" from "the server is down" cannot retry sensibly either.

## R2 — Scenario 1

**Shape:** HR (consumer) boots naturally so its real `UseSparkReplication` startup task runs; Fleet
(owner) receives the deployment its subscription worker sends.

Use the **HR → Fleet** direction. Fleet grants `Module:HR` both `Replicate/Cars` and `ReadEditNew/Car`,
so it is complete. The Fleet → HR direction is the one F15 just repaired, which makes it the better
*regression* test — add it second, once the direction that should work does.

**Make it deterministic before making it thorough:**

- Shorten the messaging retry schedule from the E2E configuration so a failed deploy reaches its
  terminal state in seconds. Default backoff would make this test minutes long.
- Poll a terminal state; never assume delivery. The chain is a detached startup task plus a polling
  subscription worker.
- Point-load where possible. Absence assertions against an eventually-consistent index pass whether or
  not the property holds — that trap has been hit three times on this branch.

**Assertions, strongest first:**

1. The owner's RavenDB has the connection string the deployment creates (licence-independent proof it
   got past authorization and reached RavenDB).
2. The deployment did **not** fail for `Forbidden` — the F15 shape.
3. The deployment did **not** fail to connect — the F14 shape.
4. *(If R1 allows)* the terminal failure is specifically the licence limitation.

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
