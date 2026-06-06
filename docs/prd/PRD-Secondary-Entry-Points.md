# PRD: Secondary Entry Points for `@mintplayer/ng-spark` and `@mintplayer/ng-spark-auth`

**Status:** Draft
**Branch:** _TBD_ (propose `feat/ng-spark-secondary-entrypoints`)
**Author:** Pieterjan
**Target packages:** `@mintplayer/ng-spark` (v0.1.2 → v0.2.0), `@mintplayer/ng-spark-auth` (v0.1.0 → v0.2.0)

---

## 1. Problem

Both libraries today export everything from a single `src/public-api.ts`. When a consumer imports any single symbol (e.g. `SparkAuthService`), the bundler pulls in the entire public API graph into that chunk:

- Shell bundle eagerly includes login/register/forgot-password/reset-password/two-factor components even though they are only reached via lazy routes.
- Every lazy-loaded route chunk that needs any spark symbol re-includes the full dependency graph (services, 22 pipes, 8 components), because with a single entry point the module boundary is the whole library.
- Even with `"sideEffects": false`, Angular's ng-packagr FESM output packages each entry point as one flat module — Rollup/esbuild can only tree-shake unused named exports within the module, not sibling components/pipes that are structurally included via standalone `imports`.

Net effect: library code is bundled multiple times across route chunks, shell bundle is larger than it needs to be, and pulling any auth symbol eagerly wires all 6 auth form components into the shell bundle (their templates drag in ng-bootstrap's form, alert, card, spinner, toggle-button components too).

## 2. Goal

Split both libraries into **secondary entry points** (a.k.a. sub-path exports) so that:

- `import { SparkAuthService } from '@mintplayer/ng-spark-auth/core'` drags in only the service + its token.
- `import { sparkAuthGuard } from '@mintplayer/ng-spark-auth/guards'` drags in only the guard.
- Lazy auth routes load `@mintplayer/ng-spark-auth/login` etc. as separate chunks, not re-bundled everywhere.

After the refactor, consumers get tree-shakeable, chunk-friendly imports. The existing root import (`@mintplayer/ng-spark-auth`) either becomes a thin re-export or is dropped in favour of explicit sub-paths.

## 3. Non-goals

- No functional/API changes to any component, service, pipe, guard, interceptor, or route factory — this is a pure packaging refactor.
- No change to runtime behaviour, no renames, no new features.
- No migration to Nx. **This is explicitly called out** because the reference implementation (mintplayer-ng-bootstrap) is Nx-based, and we are not adopting Nx here — see §5 "Nx vs. this repo".
- No change to the demo apps' runtime behaviour; only their import statements and tsconfig `paths` change.
- No changes to CI/publish pipelines beyond verifying the existing `npm run build` still produces a valid package (sub-entry discovery is automatic).

## 4. Reference implementation

`C:\Repos\mintplayer-ng-bootstrap` (commit `bd72156`) has 85 secondary entry points built with ng-packagr. The essential pattern we will copy:

- One folder per entry point at the library root (flat, not nested).
- Each entry point folder contains `ng-package.json` + `index.ts` + `src/`.
- The per-entry-point `ng-package.json` is **trivial** — just `{ "lib": { "entryFile": "index.ts" } }`.
- ng-packagr auto-discovers sub-entry-points by scanning for nested `ng-package.json` files — **no root-level listing of entry points, no per-entry build commands**.
- Cross-entry-point imports inside the library use the package sub-path (`@mintplayer/ng-bootstrap/button`) — same string consumers would use.

## 5. Nx vs. this repo — what transfers

The mintplayer-ng-bootstrap repo is an Nx monorepo; this repo (`MintPlayer.Spark`) is a .NET solution with an npm workspaces root containing plain ng-packagr library projects. What this means concretely:

| Concern | mintplayer-ng-bootstrap (Nx) | MintPlayer.Spark (npm workspaces) | Transfers? |
|---|---|---|---|
| Sub-entry discovery | ng-packagr scans `ng-package.json` files | ng-packagr scans `ng-package.json` files | **Yes — identical** (ng-packagr does this regardless of Nx). |
| Build command | `nx build mintplayer-ng-bootstrap` → invokes `@nx/angular:package` → invokes ng-packagr | `npm run build` → `ng-packagr -p ng-package.json` | **Yes** — our command stays the same; no `project.json` needed. |
| Library location | `libs/mintplayer-ng-bootstrap/` | `node_packages/ng-spark/`, `node_packages/ng-spark-auth/` | N/A — path is just where the package lives. |
| Workspace TS path alias | `tsconfig.base.json` with `"@mintplayer/ng-bootstrap/*": ["libs/mintplayer-ng-bootstrap/*"]` | No workspace-level tsconfig today; each demo app's `ClientApp/tsconfig.json` declares its own `paths` duplicated across 4 demos | **Pattern transfers directly** — we introduce a repo-root `tsconfig.base.json` and have libraries + demos extend it. `tsconfig.base.json` is a plain TS feature, not an Nx feature. |
| Demo consumption during dev | TS path alias resolves to library source, no build step needed | Same — demo apps already use `paths` to map `@mintplayer/ng-spark` to the library's `src/public-api.ts` | **Yes** — we centralise the wildcard entries in `tsconfig.base.json`. |

**Conclusion:** the ng-bootstrap pattern is fully applicable, including the shared `tsconfig.base.json`. The only real adaptation is where the per-entry-point folders sit (`node_packages/ng-spark/<entry>/` instead of `libs/mintplayer-ng-bootstrap/<entry>/`).

## 6. Current state inventory

### 6.1 `@mintplayer/ng-spark` (70 exports across 6 categories)

Organised today in `node_packages/ng-spark/src/lib/` as:

- **models/** — 15 type files + `index.ts` barrel. 27 exports (types, enums, helper fns, `SPARK_CONFIG` token, `defaultSparkConfig`, `currentLanguage` signal).
- **services/** — 4 services (`SparkService`, `SparkStreamingService`, `SparkLanguageService`, `RetryActionService`) + `index.ts` barrel (unused). All `providedIn: 'root'`.
- **components/** — 8 standalone components: `SparkPoFormComponent`, `SparkPoCreateComponent`, `SparkPoEditComponent`, `SparkPoDetailComponent`, `SparkQueryListComponent`, `SparkSubQueryComponent`, `SparkRetryActionModalComponent`, `SparkIconComponent` (plus `SparkIconRegistry` service).
- **pipes/** — 22 standalone pipes (translate, attribute value, reference value, lookup, as-detail, array, router-link, input-type, error-for-attribute, icon-name).
- **providers/** — `provideSpark()`, `provideSparkAttributeRenderers()`, `SPARK_ATTRIBUTE_RENDERERS` token, attribute renderer interfaces.
- **routes/** — `sparkRoutes()` factory, `SparkRouteConfig` interface.
- **interfaces/** — `SparkAttributeRenderer*` interfaces (detail/column/edit renderers).

Dependency facts (relevant to split boundaries):

- Models depend on nothing in the library.
- Services depend on models + `SPARK_CONFIG` token.
- Pipes: 3 depend on `SparkLanguageService`; rest are pure or model-only.
- Components depend on services + pipes + models + `SPARK_ATTRIBUTE_RENDERERS` token.
- `SparkPoCreateComponent` and `SparkPoEditComponent` both depend on `SparkPoFormComponent`.
- `SparkPoDetailComponent` depends on `SparkSubQueryComponent`.
- No circular deps.

### 6.2 `@mintplayer/ng-spark-auth` (22 exports across 8 categories)

Organised today in `node_packages/ng-spark-auth/src/lib/` as:

- **models/** — 3 files + `index.ts` barrel. Types (`AuthUser`, `SparkAuthConfig`, `SparkAuthRouteEntry`, `SparkAuthRouteConfig`, `SparkAuthRoutePaths`), tokens (`SPARK_AUTH_CONFIG`, `SPARK_AUTH_ROUTE_PATHS`), const (`defaultSparkAuthConfig`).
- **services/** — `SparkAuthService`, `SparkAuthTranslationService` (both `providedIn: 'root'`).
- **pipes/** — `TranslateKeyPipe` (depends on `SparkAuthTranslationService`).
- **guards/** — `sparkAuthGuard` (function).
- **interceptors/** — `sparkAuthInterceptor` (function).
- **providers/** — `provideSparkAuth()`, `withSparkAuth()`.
- **routes/** — `sparkAuthRoutes()` factory (lazy-loads all 6 components).
- **components/** — 6 standalone components (`SparkAuthBarComponent`, `SparkLoginComponent`, `SparkTwoFactorComponent`, `SparkRegisterComponent`, `SparkForgotPasswordComponent`, `SparkResetPasswordComponent`).

Key usage observations from Fleet/HR demos:

- Setup tier (`provideSparkAuth`, `withSparkAuth`, `sparkAuthRoutes`) is always imported together at bootstrap.
- `SparkAuthBarComponent` + `SparkAuthService` are imported into the shell (eager).
- The 5 form components are only reached via lazy routes — never imported directly in consumer code.
- DemoApp doesn't use ng-spark-auth at all.

This profile makes ng-spark-auth a **strong candidate** for secondary entry points: today the shell bundle includes all 5 form components even though they're only used via lazy routes.

## 7. Proposed entry-point layout

The splits prioritise: (a) a very small "use this at bootstrap" surface, (b) one chunk per lazy route, (c) models/types as a no-runtime-cost entry point.

### 7.1 `@mintplayer/ng-spark`

| Sub-path | Contents | Rationale |
|---|---|---|
| `@mintplayer/ng-spark` (root) | `provideSpark`, `SparkConfig`, `SPARK_CONFIG`, `defaultSparkConfig` only | Bootstrap surface — tiny, always eagerly imported. |
| `@mintplayer/ng-spark/models` | All 27 model/type exports | Pure types + enums + tokens; zero-runtime peer-dep. Imported by everything else. |
| `@mintplayer/ng-spark/services` | `SparkService`, `SparkStreamingService`, `SparkLanguageService`, `RetryActionService`, `SparkIconRegistry` | Root-provided singletons; imported wherever API calls happen. |
| `@mintplayer/ng-spark/pipes` | All 22 pipes (single group — they're small and co-used) | Could be split further, but 22 pipes as one entry point is ~the same size as ng-bootstrap's per-pipe entries combined. Revisit if measurements justify it. |
| `@mintplayer/ng-spark/renderers` | `SPARK_ATTRIBUTE_RENDERERS`, `provideSparkAttributeRenderers`, `SparkAttributeRenderer*` interfaces | App registers its custom renderers at bootstrap; isolating this lets us expose the API without dragging in any CRUD component. |
| `@mintplayer/ng-spark/routes` | `sparkRoutes`, `SparkRouteConfig` | Imported once at router setup. Uses `loadComponent` so it doesn't eagerly pull components. |
| `@mintplayer/ng-spark/po-form` | `SparkPoFormComponent` | Shared by create + edit; separated so both can lazy-load it. |
| `@mintplayer/ng-spark/po-create` | `SparkPoCreateComponent` | Lazy route target. |
| `@mintplayer/ng-spark/po-edit` | `SparkPoEditComponent` | Lazy route target. |
| `@mintplayer/ng-spark/po-detail` | `SparkPoDetailComponent` + `SparkSubQueryComponent` | Detail page uses sub-query internally — keep them together to avoid a cross-entry dep. |
| `@mintplayer/ng-spark/query-list` | `SparkQueryListComponent` | Lazy route target. |
| `@mintplayer/ng-spark/retry-action-modal` | `SparkRetryActionModalComponent` | Usually mounted once at shell level. |
| `@mintplayer/ng-spark/icon` | `SparkIconComponent` | Used in templates; keep standalone for tree-shaking. |

**Total: 1 root + 12 secondary entry points.**

### 7.2 `@mintplayer/ng-spark-auth`

| Sub-path | Contents | Rationale |
|---|---|---|
| `@mintplayer/ng-spark-auth` (root) | `provideSparkAuth`, `withSparkAuth`, `SparkAuthConfig`, `SPARK_AUTH_CONFIG`, `defaultSparkAuthConfig` | Bootstrap surface. |
| `@mintplayer/ng-spark-auth/models` | All type exports + `SPARK_AUTH_ROUTE_PATHS` token + `SparkAuthRoute*` types | Zero-runtime. |
| `@mintplayer/ng-spark-auth/core` | `SparkAuthService`, `SparkAuthTranslationService` | Used in shell + guards + every form component. |
| `@mintplayer/ng-spark-auth/guards` | `sparkAuthGuard` | Used in consumer app route definitions. |
| `@mintplayer/ng-spark-auth/interceptors` | `sparkAuthInterceptor` | Exposed for consumers that want to register manually (today it's wrapped by `withSparkAuth()` at root). |
| `@mintplayer/ng-spark-auth/pipes` | `TranslateKeyPipe` | Used inside auth components; also exportable for app templates. |
| `@mintplayer/ng-spark-auth/routes` | `sparkAuthRoutes` factory | Imported once. Uses `loadComponent` → doesn't eagerly pull component entry points. |
| `@mintplayer/ng-spark-auth/auth-bar` | `SparkAuthBarComponent` | Eager in shell. |
| `@mintplayer/ng-spark-auth/login` | `SparkLoginComponent` | Lazy route target. |
| `@mintplayer/ng-spark-auth/two-factor` | `SparkTwoFactorComponent` | Lazy route target. |
| `@mintplayer/ng-spark-auth/register` | `SparkRegisterComponent` | Lazy route target. |
| `@mintplayer/ng-spark-auth/forgot-password` | `SparkForgotPasswordComponent` | Lazy route target. |
| `@mintplayer/ng-spark-auth/reset-password` | `SparkResetPasswordComponent` | Lazy route target. |

**Total: 1 root + 12 secondary entry points.**

This granularity mirrors actual lazy-loading boundaries: the 5 form components are never imported directly, and Angular's `loadComponent` will produce one chunk per component once each lives behind its own sub-path.

## 8. Physical file layout (per library)

Mirrors the mintplayer-ng-bootstrap pattern, adjusted for our path:

```
node_packages/ng-spark/
├── src/                           # Root entry point (slimmed down)
│   └── public-api.ts              # Now only: provideSpark, SPARK_CONFIG, SparkConfig, defaultSparkConfig
├── models/
│   ├── ng-package.json            # { "lib": { "entryFile": "index.ts" } }
│   ├── index.ts                   # export * from './src';
│   └── src/
│       ├── index.ts               # Public API for this entry point
│       └── ...model files (moved from src/lib/models/)
├── services/
│   ├── ng-package.json
│   ├── index.ts
│   └── src/...
├── pipes/
│   ├── ng-package.json
│   ├── index.ts
│   └── src/...
├── po-form/
│   ├── ng-package.json
│   ├── index.ts
│   └── src/...
├── po-create/
│   ├── ng-package.json
│   ├── index.ts
│   └── src/...
├── po-edit/
│   ├── ng-package.json
│   ├── index.ts
│   └── src/...
├── po-detail/
│   ├── ng-package.json
│   ├── index.ts
│   └── src/...
├── query-list/
│   ├── ng-package.json
│   ├── index.ts
│   └── src/...
├── retry-action-modal/
│   ├── ng-package.json
│   ├── index.ts
│   └── src/...
├── icon/
│   ├── ng-package.json
│   ├── index.ts
│   └── src/...
├── renderers/
│   ├── ng-package.json
│   ├── index.ts
│   └── src/...
├── routes/
│   ├── ng-package.json
│   ├── index.ts
│   └── src/...
├── ng-package.json                # Root — unchanged signature, points to src/public-api.ts
├── package.json                   # Root — no new exports needed (ng-packagr auto-generates)
└── tsconfig.lib.json              # Unchanged
```

Identical structure for `node_packages/ng-spark-auth/` with its own 12 sub-entries.

### Per-entry-point files — exact contents

**`models/ng-package.json`:**
```json
{
  "$schema": "../node_modules/ng-packagr/ng-package.schema.json",
  "lib": {
    "entryFile": "index.ts"
  }
}
```
_(Note: `$schema` path is relative to this file's location. Since `node_modules/` lives at the repo root and the library root is `node_packages/ng-spark/`, relative path is `../node_modules/...` — verify on first entry-point creation. The ng-bootstrap pattern uses `../../../` because they're nested inside `libs/mintplayer-ng-bootstrap/`.)_

**`models/index.ts`:**
```ts
export * from './src';
```

**`models/src/index.ts`:**
```ts
export * from './persistent-object';
export * from './entity-type';
export * from './spark-query';
// ...all model exports
```

### Root `src/public-api.ts` (after refactor)

```ts
export * from './lib/providers/provide-spark';
export { SPARK_CONFIG, defaultSparkConfig, type SparkConfig } from './lib/models/spark-config';
```

Everything else moves out of the root. Consumers who were doing `import { SparkService } from '@mintplayer/ng-spark'` will get a TS error and must switch to `from '@mintplayer/ng-spark/services'`. See §11 for breaking-change handling.

## 9. Build configuration changes

### 9.1 Library side

- **`ng-package.json` (root)** — unchanged. Still `{ "dest": "dist", "lib": { "entryFile": "src/public-api.ts" } }`. ng-packagr auto-discovers sub-entries by scanning for other `ng-package.json` files under the library root.
- **`package.json` (root)** — unchanged. No `exports` field is required because ng-packagr writes a generated `package.json` into each sub-folder of `dist/` that declares `module` + `typings` for the FESM + `.d.ts` artefacts. Consumers resolve via standard Node sub-path resolution.
- **`tsconfig.lib.json`** — unchanged. The compiler discovers all `.ts` files via `include: ["src/**/*.ts"]`; we need to extend this to cover the new folders: `include: ["**/*.ts"]` with existing `exclude: ["**/*.spec.ts"]`. (Matches what ng-bootstrap uses.)
- **Each sub-entry has its own `ng-package.json`** with the trivial content shown above. No per-entry `tsconfig`.

### 9.2 Shared `tsconfig.base.json` at repo root

Rather than duplicating path aliases across four demo app tsconfigs (today's state — same `paths` block copy-pasted into `Demo/DemoApp/.../tsconfig.json`, `Demo/Fleet/.../tsconfig.json`, `Demo/HR/.../tsconfig.json`, `Demo/WebhooksDemo/.../tsconfig.json`), introduce a single `tsconfig.base.json` at the repo root and have every TS project extend it.

**How `extends` + `paths` resolution works (TS ≥ 4.1):** path mappings in `paths` are resolved relative to the tsconfig file that *declares* them, or relative to its `baseUrl` if set. When a child tsconfig uses `extends` and does **not** redeclare `paths`, it inherits the parent's paths and they resolve from the *parent's* directory. This is precisely how Nx uses `tsconfig.base.json`. We can use the exact same mechanism without Nx.

**New file: `tsconfig.base.json` (repo root):**

```json
{
  "compileOnSave": false,
  "compilerOptions": {
    "baseUrl": ".",
    "paths": {
      "@mintplayer/ng-spark": ["node_packages/ng-spark/src/public-api.ts"],
      "@mintplayer/ng-spark/*": ["node_packages/ng-spark/*/index.ts"],
      "@mintplayer/ng-spark-auth": ["node_packages/ng-spark-auth/src/public-api.ts"],
      "@mintplayer/ng-spark-auth/*": ["node_packages/ng-spark-auth/*/index.ts"]
    }
  }
}
```

Key detail: `baseUrl: "."` pins resolution to the directory containing `tsconfig.base.json` (the repo root), so the alias targets can use repo-relative paths (`node_packages/...`) instead of the brittle `../../../../` chains currently scattered across demos.

**Demo app `ClientApp/tsconfig.json` (all four demos — identical change):**

```json
{
  "extends": "../../../../tsconfig.base.json",
  "compileOnSave": false,
  "compilerOptions": {
    "strict": true,
    "noImplicitOverride": true,
    /* ...rest of compiler options, UNCHANGED... */
    "target": "ES2022",
    "module": "preserve"
    /* NOTE: no "paths" here — inherited from tsconfig.base.json */
  },
  "angularCompilerOptions": { /* unchanged */ },
  "files": [],
  "references": [ /* unchanged */ ]
}
```

Each demo is nested 4 levels deep (`Demo/<Area>/<App>/ClientApp/tsconfig.json`), so `extends` is `../../../../tsconfig.base.json`. That's a fixed string — always the same for all 4 demos — and easier to review than the current per-app `paths` duplication.

**Library `tsconfig.lib.json` (both libraries — optional but recommended):**

```json
{
  "extends": "../../tsconfig.base.json",
  "compilerOptions": {
    "strict": true,
    /* ...existing options unchanged... */
    "outDir": "./out-tsc/lib"
  },
  "angularCompilerOptions": { /* unchanged */ },
  "include": ["**/*.ts"],
  "exclude": ["**/*.spec.ts"]
}
```

Library-side `extends` is `../../tsconfig.base.json` (from `node_packages/<lib>/tsconfig.lib.json`). The library also gets the aliases for free — relevant for sibling-entry-point imports during development/IDE (see §10). At ng-packagr build time, sibling resolution may come from `dist/` rather than source, so the alias here is primarily for editor ergonomics, but costs nothing.

**Why this is better than per-demo `paths`:**
- One place to update when we add/rename a sub-entry point.
- Eliminates 4 copies of the same `paths` block.
- Aliases work identically in library source, library specs, and demo apps.
- Matches the Nx-style convention future contributors will expect.

**What this does NOT do:**
- Does not introduce Nx — no `nx.json`, no `project.json`, no `@nx/*` dependencies.
- Does not change the build commands (libraries still build via `npm run build` → `ng-packagr`; demos still build via `ng build`).
- Does not require changes to `ng-package.json` files — those don't consult tsconfig `paths`.

### 9.3 Build command

Unchanged: `cd node_packages/ng-spark && npm run build` (which is `ng-packagr -p ng-package.json`). ng-packagr prints a build plan that lists each discovered entry point, and the output `dist/` gains per-entry `package.json` + `.mjs` + `.d.ts` files automatically.

## 10. Internal cross-entry-point imports

Inside the library, code in one sub-entry that needs another sub-entry imports by the public sub-path:

```ts
// node_packages/ng-spark/po-form/src/po-form.component.ts
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SPARK_ATTRIBUTE_RENDERERS } from '@mintplayer/ng-spark/renderers';
import { AttributeValuePipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import type { EntityType, PersistentObject } from '@mintplayer/ng-spark/models';
```

Relative imports only stay within the same sub-entry. Cross-entry relative imports (`../../services/...`) are forbidden — they would bypass the entry-point boundary and cause ng-packagr to bundle the dependency into the wrong entry. **Enforced by review**; optionally add an ESLint `no-restricted-imports` rule.

For this to resolve during library build, the path alias must be visible to **ng-packagr's own TS compilation**. With the shared `tsconfig.base.json` introduced in §9.2, `node_packages/<lib>/tsconfig.lib.json` extends the base and therefore inherits the `@mintplayer/ng-spark/*` alias pointing at `node_packages/ng-spark/*/index.ts`. So sibling-entry imports resolve against source during dev/editor.

At ng-packagr build time, the behaviour is: ng-packagr builds entry points in dependency order and resolves sibling imports from already-built `dist/` artefacts (how the ng-bootstrap repo works). The `paths` alias in the inherited base may override this during ng-packagr's TS compile step — in practice ng-packagr respects its own entry-point graph and doesn't follow source aliases across entry boundaries, but **verify on first build**. If ng-packagr resolves sibling entries to source instead of dist (causing double-bundling), the fix is to override `paths` to empty in `tsconfig.lib.json` (`"paths": {}`) so the library build ignores the base aliases.

## 11. Breaking change handling

Both packages are currently **pre-1.0** (`0.1.2` and `0.1.0`) and published to npm. Consumers are:

- Internal demo apps (Fleet, HR, DemoApp, WebhooksDemo) — updated in the same PR.
- The published `@mintplayer/ng-spark-auth@^0.0.7` dep in the root `package.json` (likely stale reference — this PR should align it).
- Any external consumer.

Recommendation: **straight breaking change**, bump both to `0.2.0`. Document migration in the PR description and the package READMEs with a table of `from → to` import paths.

_Alternative rejected:_ keep `public-api.ts` as a full re-export barrel for backward compat. This defeats the entire purpose — a consumer who does `import { X } from '@mintplayer/ng-spark'` still pulls in the full graph. Not worth the maintenance cost on a pre-1.0 library.

## 12. Validation plan

Per library, verify after refactor:

1. **Build succeeds:** `npm run build` from the library root completes, produces `dist/` with per-entry `package.json` files.
2. **Entry-point count correct:** `dist/` contains one folder per secondary entry + `fesm2022/*.mjs` + `types/*.d.ts` per entry.
3. **Demos compile:** `ng build` for each of the 4 demo apps succeeds against the new imports.
4. **Demos run:** `ng serve` for each demo; smoke-test: navigate login → register → forgot-password → a PO list → a PO detail → a PO edit. Matches PRD §2 — this is the "feature works" bar, not a code-level check.
5. **Bundle inspection:** use `source-map-explorer` or `esbuild --metafile` on a demo build to confirm:
   - Shell chunk does **not** contain `SparkLoginComponent`, `SparkRegisterComponent`, `SparkForgotPasswordComponent`, `SparkResetPasswordComponent`, `SparkTwoFactorComponent`.
   - Shell chunk does **not** contain all 8 CRUD components unless the shell template references them.
   - Each lazy route has its own chunk that doesn't duplicate service/model code that's already in shell.
6. **Tree-shaking regression check:** baseline the shell bundle size before/after. Expected: measurable drop (rough estimate 15-30% for Fleet/HR since their shells pull in auth via the monolithic entry).

## 13. Implementation phases

Recommended order — each phase independently reviewable:

1. **Phase 1 — `@mintplayer/ng-spark-auth` refactor.** Smaller surface (22 exports, 12 target entry points). Lower risk. Proves the pattern on this repo.
2. **Phase 2 — Demo app migrations for ng-spark-auth.** Update Fleet + HR imports and `tsconfig.json` paths. Validate per §12.
3. **Phase 3 — `@mintplayer/ng-spark` refactor.** Larger (70 exports, 13 target entry points) but same mechanical pattern as Phase 1.
4. **Phase 4 — Demo app migrations for ng-spark.** All 4 demos; update imports and tsconfig.
5. **Phase 5 — Cleanup.** Delete `src/lib/services/index.ts` barrel (becomes dead code), delete any stale model `index.ts` inside old folders, ensure no `../../../` relative cross-entry imports remain. Update `CLAUDE.md` library-location notes if needed. Bump versions. Publish.

## 14. Risks and mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| ng-packagr fails to resolve sibling-entry imports during build | Medium | Blocks build | Pre-empt: first entry point built is `models` (no deps). Shared `tsconfig.base.json` (§9.2) gives sibling aliases for dev/IDE. If ng-packagr itself follows source aliases across entry-point boundaries (double-bundling), override with `"paths": {}` in `tsconfig.lib.json` to isolate the library build. |
| Demo apps fail to resolve `@mintplayer/ng-spark-auth/login` at dev server time | Low | Blocks dev | Wildcard `paths` entry in shared `tsconfig.base.json` (§9.2) covers all 4 demos uniformly. Restart `ng serve` + kill all `node.exe` (per existing memory note about zombie dev servers). |
| `extends` path wrong in a tsconfig (misaligned nesting) | Low | Demo/library compile error | Fixed strings: demos use `../../../../tsconfig.base.json`, libraries use `../../tsconfig.base.json`. Verify by TS compile on first edit. |
| Circular entry-point dependency discovered | Low | Refactor blocker | Dependency graph was audited (§6): no cycles. `po-detail` absorbs `sub-query`, `po-form` is a shared dep of `po-create`/`po-edit`. |
| External consumer of npm package breaks | Certain | External impact | Pre-1.0, breaking allowed. Document migration table in release notes. |
| Published `@mintplayer/ng-spark-auth@^0.0.7` in root `package.json` conflicts with workspace `0.1.0` | Unrelated to this PR | Cleanup | Root `package.json` line 24 references stale npm version — remove or align in Phase 5 cleanup. |
| ng-packagr `$schema` path in sub-entry `ng-package.json` is wrong | Low | Build warning, not blocker | Schema is for editor hints only; doesn't affect build. Fix at first build. |

## 15. Open questions

1. **Should `pipes/` be split further?** 22 pipes in one entry is still miles better than "all 22 pipes always imported". Leave as one entry for now; revisit if any single consumer pulls `@mintplayer/ng-spark/pipes` but only uses 2 pipes and bundle size shows it matters.
2. **Do we want `@mintplayer/ng-spark-auth/interceptors` as a public entry?** Today `withSparkAuth()` wraps it and no consumer imports it directly. Exposing it enables bespoke HTTP setups. Recommendation: **yes** — trivial cost, opens extensibility.
3. **Should `SparkAuthTranslationService` be internal-only?** Demo apps never inject it directly; only `TranslateKeyPipe` uses it. We could drop it from the public API. Recommendation: **keep it exported from `/core`** — external consumers may want to call `setLanguage` or load custom dictionaries.
4. **Do we keep the `models` entry point exporting the `SPARK_CONFIG` token, or only types?** Currently `SPARK_CONFIG` + `defaultSparkConfig` live in `src/lib/models/spark-config.ts`. Proposal: token + const move to root entry point (imported at bootstrap); pure types stay in `/models`. This keeps `/models` zero-runtime.

## 16. Out of scope (future work)

- Migrating to Nx (explicitly a non-goal — see §3).
- Per-pipe entry points (revisit based on bundle measurements).
- Splitting `po-detail` into `po-detail` + `sub-query` separately (current combination is intentional — §6.1 — and avoids a cross-entry dep).
- Renaming the `node_packages/` folder (memory note already explains why it isn't `packages/`).
- Moving models from `/models` into the root entry for ergonomics. Current proposal keeps `/models` separate so that type-only consumers (e.g. a generator or schema tool) can import types without pulling in any runtime code.
