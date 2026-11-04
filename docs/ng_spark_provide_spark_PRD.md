# ng-spark: `provideSpark(withX(), …)` — PRD

Status: **draft, 2026-11-04.** Issue #560. Delivered **in the #238 search PR** (owner, 2026-11-04: "I would
prefer to address it in this pull-request"); its milestone is M-P in [spark_search_plan.md](spark_search_plan.md).

## 1. What exists

- About 20 `provideSpark*` functions across ng-spark's secondary entry points, e.g. `provideSparkAuth`
  (`auth/src/lib/provide-spark-auth.ts:14`), `provideSparkClientOperations` (`client-operations/src/provide.ts:42`),
  `provideSparkBoards` (`boards/src/provide-spark-boards.ts:19`), `provideSparkAttributeRenderers`
  (`renderers/src/spark-attribute-renderer-registry.ts:74`), `provideSparkHydration`, `provideSparkSeo`,
  `provideSparkServiceWorker`, `provideSparkModeration`, `provideSparkHistory`, `provideSparkContributions`,
  `provideSparkSoftDelete`, `provideSparkDashboards`, `provideSparkNotifications`, `provideSparkScoreRenderers`,
  `provideSparkTileRenderers`, `provideSparkClientMethods`, `provideSparkAccountProfileFields`, and the panel
  registrations (`panels/src/spark-panels.ts:138-183`). Return types are mixed (`Provider[]`, `Provider`,
  `EnvironmentProviders`).
- The root `provideSpark(config)` (`src/lib/provide-spark.ts:4`) only provides `SPARK_CONFIG`; no app calls it.
- HTTP features `withSparkAuth()`, `withSparkLanguage()`, `withSparkTimezone()` are `HttpFeature`s for `provideHttpClient`.
- Usage: 17 `.ts` files under `apps/` and 43 Markdown files under `docs/` + the ng-spark README call `provideSpark*`.
  MintPlayer's `app.config.ts` has seven separate Spark calls.

## 2. Design

```ts
provideHttpClient(...withSparkLanguage(), ...withSparkAuth(), ...withSparkTimezone()),   // unchanged
provideSpark(
  withAuth(),
  withClientOperations(),
  withBoards(),
  withAttributeRenderers(mintPlayerRenderers),
  withHydration(),
  withSeo(mintPlayerSeo),
  withOpenSearchDescription({ title: 'MintPlayer' }),   // spark_search_PRD.md D11
),
```

- **P1 — Feature objects.** `withX(...)` returns `SparkFeature = { kind, order, providers, requires? }`, exported
  from the feature's own entry point, so tree-shaking is unchanged.
- **P2 — HttpClient stays the app's** (owner: "I wouldn't do that"). `withSpark*()` HTTP features keep their names
  inside `provideHttpClient(...)`; Spark features drop the `Spark` prefix (`withAuth()`), so the names never clash.
- **P3 — Order is the feature's, not the app's** (owner: "The spark-feature carries the order/importance/priority
  number. Internally the features are sorted"). `provideSpark` sorts by `order` (stable), so the app may list
  features in any order. Order constants live next to `SparkFeatureKind`.
- **P4 — Validation.** Duplicate `kind` → error naming the feature, except multi-registration features
  (renderers, panels, actions, client methods, profile fields), which merge. `requires` adds missing required
  features (e.g. `withOpenSearchDescription` → `withSearch`).
- **P5 — Config.** `provideSpark` takes features only; today's `Partial<SparkConfig>` becomes `withConfig({...})`.
- **P6 — Server rendering stays separate** (owner: "Fine I guess"): `provideSparkServerRendering()` remains in
  `app.config.server.ts`.
- **P7 — Routes stay separate** (owner: agreed): `sparkRoutes()`, `sparkModerationRoutes()`, `sparkSearchRoutes()`.
- **P8 — No backward compatibility.** Every `provideSpark*` that becomes a feature is removed, not deprecated;
  the versioner assigns the bump (minor inside Angular 22, per the repo's versioning rules).
- **P9 — Return type.** `provideSpark` returns `EnvironmentProviders` (`makeEnvironmentProviders`), so it is only
  valid at the application/route-environment level, as today's mixed functions should have been.

## 3. Open questions

- **Q1** — Do any `provideSpark*` functions get called in route-level `providers` or component `providers` today
  (where `EnvironmentProviders` would not be allowed)? Spike SP1.
- **Q2** — The exact order numbers: derived from the current order in MintPlayer/HR/Fleet configs plus the known
  constraints (hydration and service worker), confirmed by SP1.

## 4. Spikes

- **SP1** — inventory every `provideSpark*` call site (apps, ng-spark tests, docs), its injector level, and any
  order dependency.

## 5. Acceptance

- No `provideSpark*` call left in `apps/` except `provideSparkServerRendering`; each app config has one
  `provideSpark(...)` call.
- A duplicate feature throws with the feature's name; a missing required feature is added.
- Listing features in reverse order gives the same providers order (unit test).
- Guides and the ng-spark README use the new form.

## 6. Decision log

| # | Decision | Evidence |
|---|---|---|
| — | In the #238 PR, issue #560 | owner, 2026-11-04 |
| P2 | HttpClient stays the app's; `withSpark*` vs `with*` naming | owner, 2026-11-04 |
| P3 | Features carry their order; sorted internally | owner, 2026-11-04 |
| P6, P7 | SSR and routes stay separate | owner, 2026-11-04 |
| P8 | No backward compatibility | owner, 2026-11-04 |
