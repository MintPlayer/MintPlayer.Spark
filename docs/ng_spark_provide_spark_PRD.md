# ng-spark: `provideSpark(withX(), …)` — PRD

Status: **implemented, 2026-11-04** (M-P done; lib, spec and app type-checks clean). Issue #560. Delivered **in the #238 search PR** (owner, 2026-11-04: "I would
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

### 4.1 SP1 results (2026-11-04)

- **Q1 — answered: every app call is application-level** (`ApplicationConfig.providers` in SparkId, QnA,
  MintPlayer, SeoTester, HR, Fleet, DemoApp, CodeCoverage). Other callers: the auth setup template
  (`libs/authorization/.../Targets/spark-auth.setup.template.ts:34`), ng-spark features calling each other's
  registration helpers (Auth → ClientMethods; SoftDelete → QueryListActions + DetailActions; Moderation →
  DetailActions + DetailPanels; Contributions → QueryRowActions + DetailActions; History → DetailPanels;
  Dashboards → DetailLayouts; Boards → TileRenderers + DetailActions; ScoreRenderers → AttributeRenderers), and
  `TestBed` specs. So `EnvironmentProviders` is safe (P9); internal composition keeps using plain `Provider[]`
  helpers that are no longer exported as `provideSpark*`.
- **Q2 — answered: three real order constraints, all resolvable inside `provideSpark`.**
  1. `provideSparkAttributeRenderers` is **not multi**: one value, last provider wins; `provideSparkScoreRenderers`
     wraps it ("use one or the other"). → `withAttributeRenderers(...)` and `withScoreRenderers(...)` become
     multi-registration features whose arrays `provideSpark` **merges** into one value, app renderers first so an
     app renderer replaces a built-in one by name.
  2. `provideSparkSeo` provides `TitleStrategy`; Angular's `TitleStrategy` is `providedIn: 'root'`, so an
     environment provider overrides it regardless of position. No order needed.
  3. `provideSparkServerRendering` replaces `HttpBackend`, must follow `provideHttpClient`; it stays a separate
     server call (P6), merged after the browser config, so unchanged.
  - Last-registration-wins per id for tile renderers and panels/actions stays; panels/actions already sort by
    their own `order`/`priority`. The only initializer is ServiceWorker's `provideAppInitializer`.
  - Feature order constants: config 0, hydration 10, service worker 20, auth 100, client operations 110, then
    feature modules 200+ (boards, dashboards, history, moderation, contributions, soft delete, notifications,
    search), registrations 500+ (renderers merged last), SEO 900.
- **Usage counts:** 48 Markdown files in `docs/`, 2 ng-spark READMEs (`README.md`, `auth/README.md`).

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
| P10 | Renderer sets merge through a multi token, `SPARK_ATTRIBUTE_RENDERER_SETS`; `SPARK_ATTRIBUTE_RENDERERS` is a factory that flattens the sets (in feature order) and appends `sparkCoreRenderers`. `withScoreRenderers` is `withAttributeRenderers([...renderers, ...sparkScoreRenderers], builtInRegistrations = 510)`, so the app's set (500) comes first and wins by name. Replaces SP1's "merges into one value" without `provideSpark` knowing about renderers | `renderers/src/spark-attribute-renderer-registry.ts`; score-renderers spec "merges with the app's renderers" |
| P11 | A feature that composes another spreads that feature's `.providers` (SoftDelete → `withQueryListActions(...).providers`), not an unexported `Provider[]` helper. **Supersedes** SP1's "plain `Provider[]` helpers" note | `soft-delete/src/provide-spark-soft-delete.ts`; no `provideSpark*` export left besides `provideSparkServerRendering` |
| P12 | The generated `spark-auth.setup.ts` exports `setupSparkAuth(config)` returning `withAuth(config)` (was `setupSparkAuthProviders` returning a provider array). The file is written only when missing, so existing apps' local copies are edited by hand | `libs/authorization/.../Targets/spark-auth.setup.template.ts` |
| P13 | Historical docs (release notes, issue PRDs/plans, `docs/prd/`) keep the old names; guides, READMEs and code comments use the new ones | owner rule: document decisions as made |
