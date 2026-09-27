# Plan — repository existence parity (#453)

**Implements:** [coverage_repo_existence_parity_PRD.md](coverage_repo_existence_parity_PRD.md) ·
**Issue:** [#453](https://github.com/MintPlayer/MintPlayer.Spark/issues/453) ·
**Branch:** `fix/issue453-repo-existence-parity` · **Base:** `master` @ `736dd141` ·
**Release:** server deploy of CodeCoverage; `libs/` changes → all NuGet packages bump their preview number
(major stays 11), npm packages bump patch/minor within 22. ·
**Status:** M1–M8 built in [PR #457](https://github.com/MintPlayer/MintPlayer.Spark/pull/457), CI green;
⏳ post-merge checklist below still open

One pull request, per `CLAUDE.md`. Tests run once, after M6.

## Milestones

### M1 — `[SparkAuthorize]` defers to security.json (PRD D1)
- `libs/spark/MintPlayer.Spark/Services/SparkAuthorizeAttribute.cs`: set `Policy = PolicyName` in every ctor.
- Register the policy where Spark registers authorization (`SparkMiddleware.cs` ~:61): a requirement that
  always succeeds, so the attribute's security.json handler is the only gate. Anonymous refused → still
  challenged/401 by the existing handler path.
- Audit every `[SparkAuthorize]` use (libs/, apps/): confirm none silently opens. Record the table in the PR.

### M2 — lookups readable through a readable type (PRD D2)
- `libs/spark/MintPlayer.Spark/Endpoints/LookupReferences/Get.cs`: allow when `Read/LookupReferences`
  **or** the caller may Read some entity type with an attribute bound to that lookup (resolve via the
  model loader). Refusal still via `SparkDenial`.
- `ng-spark/po-detail/src/spark-po-detail.component.ts` `loadLookupReferenceOptions`: a failed lookup
  degrades to showing the raw value rather than erroring the page.

### M3 — invisible ≡ missing in the resolver (PRD D3, D4)
- `IRepositoryResolver.ResolveAsync` gains a required visibility argument (`Func<Repository,bool>`).
  *(Corrected 2026-09-27: the plan first said "everything visible" for credentialed upload paths; as
  built there is no such escape — see As built.)* An invisible live hit falls through to the alias step; an
  invisible alias counts as absent; the GitHub step's loaded result is filtered the same way. Caching
  unchanged, so both paths are cached identically. Rewrite the comment at :77-89 to state the real
  guarantee.
- Callers: Badge (public, or private with a matching badge token), Browse (viewer's owner set),
  RepoSettings (viewer's manage rights, or keep today's behaviour if it authorizes after resolving —
  but it must not answer differently for private vs missing), ForkUploads/Uploads (credential decides —
  *as built* Uploads passes the token's scope check and ForkUploads public-only; both keep their
  constant 404).
- Browse: fetch the viewer's owners **before** resolving, unconditionally (authenticated only).

### M4 — one canonical refusal on Browse (PRD D5)
- `BrowseController`: the "not visible" and "unknown forge" arms return `SparkDenial.RefuseJson`
  (anonymous 401 `{"error":"Authentication required"}`, authenticated 404 `{"error":"Not found"}`).
  Same for account endpoints where an account is not visible.

### M5 — parity tests (PRD D6)
- Spark tests: `SparkAuthorizeAttribute` — anonymous with an anonymous grant passes; without, refused;
  lookup allowed via a readable type, refused otherwise.
- CodeCoverage.Tests: a theory over {private live name, private alias, made-up name under known owner,
  unknown owner, unknown forge, private by id} × {anonymous, authenticated non-member} asserting
  byte-identical status/body/relevant headers for Browse, badge, RepoSettings; a test that private and
  missing names under a known owner both reach the forge lookup (fake GitHub client); anonymous Browse
  on a public repo → 200; `/api/me/**` still refuses anonymous.
- E2E if an existing error-leakage suite covers `po/load`/Browse: extend it.

### M6 — release hygiene
- Bump `libs/**` versions per the lockstep convention (check `git log -S"preview." -- 'libs/**/*.csproj'`),
  npm packages touched → patch/minor within 22.
- `--spark-verify-model` for CodeCoverage (any doc-comment change on a modelled entity).
- Run all test projects of `MintPlayer.Spark.slnx` plus CodeCoverage.Tests, once.

### M7 — SPARK020, attribute form (PRD D7) · `cd8c6748`
- `libs/source_generators/MintPlayer.Spark.SourceGenerators/Diagnostics/AuthorizeAttributeAnalyzer.cs`:
  **error** when ASP.NET Core's exact `AuthorizeAttribute` carries a policy (ctor argument or `Policy =`)
  or `Roles =`, on classes, methods and route-handler lambdas; only in compilations that resolve
  `SparkAuthorizeAttribute`. Bare, schemes-only, `[SparkAuthorize]` and user subclasses are not reported.
- Documented in `docs/diagnostics.md`, `docs/guide-controllers.md`, `libs/spark/MintPlayer.Spark/AGENTS.md`.

### M8 — SPARK020, `RequireAuthorization` form (PRD D7) · `a02547dc`
- The same rule on ASP.NET's `AuthorizationEndpointConventionBuilderExtensions.RequireAuthorization`,
  resolved through the semantic model (one generic `TBuilder` match covers routes and groups): any policy
  name expression, or a `new AuthorizeAttribute(…)` argument carrying a policy or roles (reported on that
  argument). No-arg, `SparkAuthorizeAttribute`, schemes-only and the policy-object / builder overloads
  are allowed.

**As built (M7–M8).** One descriptor, `SPARK020`; the message format takes the written form and the
replacement as arguments, so it reads "`[Authorize]` with …" or "`RequireAuthorization` with a policy
name …" and names the matching `[SparkAuthorize(…)]` / `.RequireAuthorization(new SparkAuthorizeAttribute(…))`.
`RequireRole` inside a policy-builder lambda is a documented gap, not reported. SourceGenerators.Tests
349 green, 47 of them in `AuthorizeAttributeAnalyzerTests`. The solution builds with no hits: the only
existing uses are the schemes-only `[Authorize]` on `UploadsController`, `RequireAuthorization()` and
`RequireAuthorization(new SparkAuthorizeAttribute(…))`. A deliberate probe in CodeCoverage failed the
build, so the analyzer does reach the apps.

## As built — where it differs from the plan
- **M1.** The policy is registered through a public `SparkAuthorizeAttribute.AddPolicy(AuthorizationOptions)`,
  called by `AddSpark`. A host that wires authorization by hand (the attribute's own unit tests do) must
  call it too, or the first request throws "policy not found". Audit: Browse opens to anonymous as
  security.json intends; MeController stays closed by its separate `[Authorize]`; RepoSettings and
  Uploads hold no anonymous grant (Uploads also keeps its scheme `[Authorize]`).
- **M3.** No "all visible" argument exists — every caller's check moved *into* the resolver call:
  Uploads passes the token's scope check (a token holder probing another org's private names had the
  same oracle), ForkUploads (anonymous) passes public-only, RepoSettings passes membership of the
  viewer's owner set (fetched before resolving; equivalent to the `IsOwnerAllowedAsync` it replaces).
  M3 and M4 share a commit because both rewrite `BrowseController`'s refusal path.
- **M4.** On the account endpoints only the unknown-forge arm switched to `SparkDenial`; a missing
  account stays 404, because accounts are anonymously listable anyway (PRD §4).
- **M5.** The anonymous end-to-end cases live in `SparkAuthorizeEndToEndTests`, not a new class: a second
  `CoverageWebHostFixture` class would boot a second host concurrently, which Spark's process-wide state
  does not survive. The authenticated-non-member half is controller-level against the real resolver
  (`RepositoryExistenceParityTests`) — the test host has no GitHub sign-in. The Fleet
  `ErrorLeakageTests` suite does not cover Browse, so it was not extended.
- **M6.** NuGet: the 23 `MintPlayer.Spark*` packages → `11.0.0-preview.89` (SocketExtensions is its own
  line, untouched). npm: `@mintplayer/ng-spark` `22.21.0` → `22.22.0`, a minor like the previous bumps.
  `--spark-verify-model`: in sync, no model change.

## Post-merge
- [ ] Deploy CodeCoverage to coverage.mintplayer.com.
- [ ] Re-run the anonymous badge timing probe on production: a private name under a known owner vs a
      made-up name under the same owner. Both should now pay the GitHub round trip (was ~4 ms vs ~272 ms).
- [ ] Anonymous visit of a public repository page (`/po/repository/…`) stays on the page, with no 401
      from `lookupref/DeleteBranchPolicy` or `/api/browse/**`.

## Notes
- The owner's uncommitted edit to `spark-auth.interceptor.ts` (redirect commented out) must never be
  committed; it is not part of this change.
