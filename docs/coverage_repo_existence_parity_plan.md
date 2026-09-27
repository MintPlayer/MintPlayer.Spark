# Plan — repository existence parity (#453)

**Implements:** [coverage_repo_existence_parity_PRD.md](coverage_repo_existence_parity_PRD.md) ·
**Issue:** [#453](https://github.com/MintPlayer/MintPlayer.Spark/issues/453) ·
**Branch:** `fix/issue453-repo-existence-parity` · **Base:** `master` @ `736dd141` ·
**Release:** server deploy of CodeCoverage; `libs/` changes → all NuGet packages bump their preview number
(major stays 11), npm packages bump patch/minor within 22. ·
**Status:** M1–M6 in progress

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
- `IRepositoryResolver.ResolveAsync` gains a visibility argument (`Func<Repository,bool>`; "everything
  visible" for credentialed upload paths). An invisible live hit falls through to the alias step; an
  invisible alias counts as absent; the GitHub step's loaded result is filtered the same way. Caching
  unchanged, so both paths are cached identically. Rewrite the comment at :77-89 to state the real
  guarantee.
- Callers: Badge (public, or private with a matching badge token), Browse (viewer's owner set),
  RepoSettings (viewer's manage rights, or keep today's behaviour if it authorizes after resolving —
  but it must not answer differently for private vs missing), ForkUploads/Uploads (credential decides;
  pass all-visible and keep their constant 404).
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

## Notes
- The owner's uncommitted edit to `spark-auth.interceptor.ts` (redirect commented out) must never be
  committed; it is not part of this change.
