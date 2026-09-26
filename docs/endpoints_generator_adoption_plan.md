# Plan — Endpoints generator everywhere, and the package upgrade under it

Companion to [`endpoints_generator_adoption_PRD.md`](endpoints_generator_adoption_PRD.md).
Branch: `feat/upgrade-generators-and-endpoints`. **One pull request**, per the repository rule — the
three package migrations and the endpoint adoption land together, because Endpoints 11.2.0-rc.0 is
built on SourceGenerators.Tools 12.1.0 and they cannot be separated.

**Status:** M0–M9 done; CI green. See M0 for the one substitution made along the way, and
*Follow-ups* for what is deliberately left.

The solution builds green from clean, 0 errors, and no MPEP / MINT / MPA / SPARK diagnostic fires.
Every hand-mapped route is now a generator endpoint class except the two documented webhook
exceptions. Design settled: the 14 generic auth routes are mapped with `MapEndpoint<T<TUser>>()`
from inside `MapSparkIdentityApi<TUser>` (PRD D1) — **no application changes of any kind**, because
`AddAuthentication<TUser>` already states the user type and remains the single source of truth.

---

## ⚠️ M0 — Snapshot the route table BEFORE anything else

`tests/MintPlayer.Spark.Tests/Endpoints/RouteTableCompletenessTests.cs:99` reflects over `IMemberOf<>`
to compose prefixes. It is the safety net that would catch a route regression — and the migration
deletes the interface it reflects over, so it is rewritten by the change it polices. It cannot be
"rewritten first and proven green on the old package": `MemberOfAttribute<>` does not exist in
10.0.0.

⚠️ This matters more under D1 than it would have otherwise: 11.2.0-rc.0 resolves membership from
`[MemberOf<T>]` only, so a **stale `IMemberOf` does not fail the build — it silently maps at the root
with no prefix.** Something has to catch that.

### ⚠️ What was actually done — the fixture was NOT created

The plan called for dumping `EndpointDataSource` to a committed fixture on master and diffing after.
**That fixture does not exist.** `RouteTableCompletenessTests` was used instead, and the reasoning is
recorded here rather than left as a silent substitution:

- it already derives every route the way registration does, so a second mechanism would duplicate it;
- it asserts each route against **four** hand-maintained companions — the protocol client, the
  README route table, the API specification and the deny-all mirror — which is a stronger claim than
  "the set did not change";
- it carries `The_discovery_itself_finds_endpoints`, a guard against exactly the failure feared here:
  the day the marker changes shape, the suite fails loudly instead of vacuously passing on an empty
  set.

It ran green after the membership flip (49 tests), and again in CI. So the risk is covered — but by a
different instrument than the one planned, and a reviewer looking for a fixture will not find one.

**Not covered by that substitution:** metadata. The completeness test compares route *patterns*, not
the antiforgery and authorization metadata attached to them. That was verified instead by driving a
real browser — an authenticated POST without an `X-XSRF-TOKEN` still answers 400 — and by the
existing `XsrfSurfaceTests`.

---

## Spikes

- **SP1 — does the generated mapping change for raw endpoints?** Spark is 100% `EndpointLevel.Raw`
  and the generator has a distinct Raw branch (`Producer.cs:218,255,472`). Build before/after and
  `git diff` the emitted `EndpointMapping.g.cs` for the three libs. Proves the 42-site flip is
  metadata-neutral. Run inside M3.
- **SP2 ✅ RESOLVED** — the two webhook routes stay hand-mapped (PRD D4). `Path` is `static` with no
  `IServiceProvider`, so a configured path cannot be expressed; and route 1 is a third-party Octokit
  extension doing HMAC validation. M8 becomes documentation, not a migration.
- **SP3 ✅ RESOLVED** — `IsEnabled` *cannot* express `WithOidcCors` (it would unmap `/connect/token`
  when CORS is off). A group `Configure` reading options through
  `((IEndpointRouteBuilder)group).ServiceProvider` reproduces it exactly (PRD D3).
- **SP4 — how many of the 156 `BeEquivalentTo` sites are vacuous?** Each one the guard throws on is a
  test that was asserting nothing. Triage as **findings**. Run inside M4.
- **SP5 ✅ RESOLVED in M5.** `localCredentials` was a closed-over method parameter; endpoint classes
  read `IOptions<>` instead. Production already passed `options.Value.LocalCredentials` into that
  parameter, so the value is unchanged — but `ExternalLoginManagementTests` configured the option to
  one thing and passed the parameter another, and the last-credential guard then read whichever the
  test was not asserting about. Exactly one test failed (2490 passed, 1 failed), and the fix was to
  configure the option: a test asserting against a split between parameter and configuration was
  asserting against a state no deployment can produce.
- **SP4 note:** the Assertions vacuity guard fired on **nothing** across the 2491 tests in
  `MintPlayer.Spark.Tests`. The remaining four test projects are still to run at M9.

---

## Milestones

Commit per milestone. **Test suites run once, at M9** — intermediate milestones are verified by
building and by reading the code.

### ✅ M1 — Package versions and the Roslyn floor *(done)*

Seven packages, all five generator packages moving **together** (a mixed set lets
`ResolvePackageFileConflicts` keep the bogus `99.9.9.0` Tools dll and every generator then runs
against the old Tools).

| Package | From | To |
|---|---|---|
| MintPlayer.AspNetCore.Endpoints | 10.0.0 | 11.2.0-rc.0 |
| MintPlayer.SourceGenerators(.Attributes/.Tools) | 10.22.0 / 10.20.1 / 10.21.0 | 12.1.0 |
| MintPlayer.ValueComparerGenerator(.Attributes) | 10.20.2 / 10.20.1 | 12.1.0 |
| MintPlayer.Assertions | 1.0.0 | 11.0.0-rc.5 |

Restore then failed NU1605/NU1107 — Tools 12.1.0 carries Roslyn 5.9.0 against Spark's 5.3.0 pins.
Nine pins moved to 5.9.0 across the three generator projects and the generator test harness.

### ✅ M2 — SourceGenerators 12.1.0 code migration *(done — 30 files)*

The 91 compile errors, one family:

- delete `using MintPlayer.SourceGenerators.Tools.ValueComparers;` (13 files)
- `[AutoValueComparer]` → `[GenerateEquality]`, `[ComparerIgnore]` → `[EqualityIgnore]` (~25 usages)
- drop the third `IncrementalValueProvider<ICompilationCache>` parameter from every `Initialize`
- delete `.WithComparer(...)` / `.WithNullableComparer()` (8 sites). ⚠️ A call ending in `);`
  terminates its statement — it must leave a `;` behind, not simply vanish
- then `EquatableArray<T>` where a pipeline step builds a new collection, and `partial` on any
  derived or containing type of a `[GenerateEquality]` type (MINT003/MINT006)

⚠️ Dictionaries now compare **order-insensitively** — check any generator whose caching depended on
ordering. Build green before M3.

### ✅ M3 — Endpoints 11.x: the membership flip *(done — 38 declarations, 34 files, + 2 test files)*

42 sites: delete `, IMemberOf<G>`, add `[MemberOf<G>]`. 33 endpoints + 9 groups across three libs.
Double-membership (CS0579) risk nil — no Spark endpoint or group has a base class.

Plus the two tests that will not compile: `SparkAuthGroupTests.cs:17-19` (three assignability
assertions → attribute checks honouring `inherit: true`) and `RouteTableCompletenessTests.cs:99`
(reflection over `IMemberOf<>` → `MemberOfAttribute<>`; doc comments at `:10` and `:95` name it too).

Then run **SP1**. Nothing else in the 11.x break list applies — Spark is raw arity-0 throughout.

### M4 — Assertions 11.0.0-rc.5 fallout

Build first: **MPA0001 is severity `Error`** and can fail it. Then run the suites and work SP4.

### ✅ M5 — The 14 auth routes become generic endpoint classes *(done)*

Per PRD D1 and D2. Entirely inside `MintPlayer.Spark.Authorization`; **no app changes**.

- new endpoint classes generic over `TUser` (`where TUser : SparkUser, new()`, **restated on each
  class** — constraints are not inherited from the mapping method), `internal sealed partial`, all
  carrying `[MemberOf<SparkAuthGroup>]`, in `Endpoints/Passkeys/` and `Endpoints/ExternalLogin/`
- **no new group classes** — the three gates stay as `if`s around the `MapEndpoint` calls, exactly
  where they are today (PRD D2)
- map them beside the existing call:
  ```csharp
  endpoints.MapSparkAuthEndpoints();          // the 4 non-generic, as today
  endpoints.MapEndpoint<Passkeys<TUser>>();   // …and the generic ones
  ```
- ⚠️ **delete the hand-mapped routes in the same commit** — `PasskeyEndpoints.cs` and the blocks in
  `SparkAuthenticationExtensions.cs`
- write every `Path` **relative** to its group or MPEP010 fires and routes land at
  `/spark/auth/spark/auth/…`
- keep all metadata per-endpoint; leave group `Configure` empty so metadata sets stay byte-identical
  for `SparkAntiforgeryMiddleware`
- resolve **SP5** here

`GetAuthCapabilities` keeps working — it reads the runtime `EndpointDataSource`
(`GetAuthCapabilities.cs:32`), which includes manually mapped routes.

### ✅ M6 — DF1 and DF2 *(done)*

`IdentityUserType`'s inconsistent null handling: make `Token.cs:679` and `UserInfo.cs:64` fail closed
like `Login.cs:84`, `Logout.cs:26`, `TwoFactor.cs:60`, instead of `?? typeof(SparkUser)` — which
resolves `UserManager<SparkUser>` from a container holding `UserManager<AppUser>` and throws at
request time for any app with a derived user type. Delete the stale root-mapping comment at
`SparkAuthenticationExtensions.cs:147-148`.

### ✅ M7 — `MintPlayer.Spark.IdentityProvider`, 16 routes → 16 classes *(done)*

Per PRD D3. No closing machinery — every handler is non-generic.

Opt the assembly in, copying Replication exactly:

1. `<PackageReference Include="MintPlayer.AspNetCore.Endpoints" Version="11.2.0-rc.0" />`
   (`…Replication.csproj:30`), plus the global-usings block from `…Authorization.csproj:42-47`
   (`Microsoft.AspNetCore.Builder`, `.Http`, `.Routing`) that the generator's output needs
2. a new `AssemblyInfo.cs` with `[assembly: EndpointsMethodName("MapSparkIdentityProviderEndpoints")]`
3. replace `SparkIdentityProviderExtensions.cs:87` with
   `builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkIdentityProviderEndpoints());`

Then the four groups, 16 classes (GET and POST on one route are two classes), and the deletions:
`MapIdentityProviderEndpoints` (`:191-230`), `WithOidcCors` (`:186-189`) and `RequireAntiforgery`
(`:238`).

⚠️ Handlers return `Task`, not `Task<IResult>` — wrap as
`{ await X.Handle(httpContext); return Results.Empty; }`. ⚠️ The CORS group `Configure` uses a cast
through an explicit interface implementation; it works but is unsanctioned by the Endpoints library.
File the upstream ask for `Configure(RouteGroupBuilder, IServiceProvider)` alongside.

### ✅ M8 — `MintPlayer.Spark.Webhooks.GitHub`: record the exception *(done — docs/endpoints_generator_webhooks_exception.md)*

Per PRD D4 — **not a migration**. Both routes stay hand-mapped and the assembly takes no
`PackageReference`. Document why in `docs/`, and file the upstream ask for a provider-aware or
instance `Path`.

⚠️ The old motivation for converting route 2 is **stale**: the "bare `Map()` matching every mutating
verb" hole was already fixed (`SparkBuilderExtensions.cs:72` is `MapGet`, recorded in
`docs/release-notes-preview-87.md:71`). Do not re-open it on that basis.

### ✅ M9 — Docs, version gate, and the full sweep

**Docs done:** `docs/diagnostics.md` gained an `MPEP*` section (deliberately separate from the
`SPARK*` table — those are ours, these come from a referenced package), and M8's exception doc is
written.

**Version gate done:** all **14** packable `libs/` projects changed on this branch moved to
`11.0.0-preview.88`. The gate checks each project individually, not "did something bump".

✅ **The sweep is green on CI.** Every local failure turned out to be environmental, and the runner
proves it — which is the point of "check CI, not just the local suites".

| Project | Local | CI |
|---|---|---|
| `MintPlayer.Spark.Tests` | 2490 ✅ / 1 ✗ | **2491 ✅** |
| `CodeCoverage.Tests` | 383 ✅ / 390 ✗ | **773 ✅** |
| `MintPlayer.Spark.E2E.Tests` | 104 ✅ / 1 ✗ | **105 ✅** |
| `MintPlayer.Spark.SourceGenerators.Tests` | 302 ✅ | **302 ✅** |
| `MintPlayer.Spark.Client.Tests` | 91 ✅ | **91 ✅** |

The three local failures, all environmental, all confirmed by the runner passing:

- **ETL deployment** — `LicenseLimitException`; Community has no ETL, Developer does. The request
  routed, passed module-certificate validation and reached `EtlTaskManager`, which is itself evidence
  `EtlDeploy`'s migration works.
- **CodeCoverage ×390** — `ServerDirectory` null; the `7.2.1` provisioned server directory was
  deleted mid-session and had not re-provisioned.
- **Corax complex-map** — expects indexing errors and got none; unrelated to endpoints.

⚠️ **CI also caught one thing local runs did not:** the auth-bar change broke its own spec, because
the component's constructor now calls a service method the test double predated. Fixed, with the gate
itself covered.

**SP4 — no vacuity findings.** The Assertions guard fired on nothing across 2491 + 773 + 105 + 302 +
91 tests.

---

## Sequencing notes

- M0 before everything — the net must exist before the interface it hangs from is deleted.
- M2 before M3: nothing builds until the SourceGenerators migration is done, so no Endpoints error is
  even visible yet.
- M5's two halves — add the classes, delete the hand-mapped routes — are one commit, not two.
- M7 and M8 are independent of M5 and can be reordered.

## Risks

- **A partial package upgrade is worse than none** (the 99.9.9.0 conflict-resolution trap). Never
  land M1 split across commits.
- **A stale `IMemberOf` does not fail the build**; it silently maps at the root. M0 exists for this.
- **`RouteTableCompletenessTests` is both the net and the thing being changed.**
- **Verification by grep is not evidence for route behaviour** — the route table must be compared as
  data, not inspected by eye.
- **CI-only gates**: model/description sync and the `libs/` version bump. This PR touches `libs/`, so
  the version bump is mandatory — and per `CLAUDE.md`, Spark's majors track **net11.0** and do **not**
  move because a dependency's major did.

## Follow-ups — not blockers, but real

Two upstream asks, neither filed yet, both recorded where the workaround lives:

1. **`IEndpointGroup.Configure(RouteGroupBuilder, IServiceProvider)`.** `IsEnabled` receives the
   provider and its sibling does not, so option-dependent *metadata* has to reach it by casting
   through `IEndpointRouteBuilder`, whose members `RouteGroupBuilder` implements explicitly. The
   higher-value half is a **doc fix**: the README's answer for "options-dependent" is `IsEnabled`,
   which for metadata is wrong in a dangerous direction — following it makes `/connect/token`
   disappear when CORS is off, rather than merely losing a header.
2. **A provider-aware or instance `Path`**, which is the single thing blocking the dev-WebSocket
   route in `docs/endpoints_generator_webhooks_exception.md`.

One limitation inside this repository, documented in `SparkAuthBarComponent`:
`SPARK_AUTH_ROUTE_PATHS` is provided on the `sparkAuthRoutes()` subtree, but the auth bar lives in
the application shell, outside it. The bar therefore gates on the server's capability and falls back
to `withPasskeys()`'s default path — so an application that gives the passkey page a **custom** path
*and* renders the library bar gets the wrong link. Fixing it means exposing mounted paths at root
rather than per-route, which changes a published API.

Not re-measured: passkey coverage after the 20 new tests. It was 30.4% on the commit that prompted
them.

## Out of scope

Carried from the PRD: SpaServices.Xsrf; Spark's own XSRF mint placement; modernising the 7 hand-read
route values and 4 hand-deserialised bodies to `[RouteParam]`/typed requests; changing what any
endpoint does.
