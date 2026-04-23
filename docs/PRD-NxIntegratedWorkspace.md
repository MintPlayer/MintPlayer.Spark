# PRD: Integrated Nx Workspace Alongside the .NET Solution

| | |
|---|---|
| **Version** | 1.1 |
| **Date** | 2026-04-23 |
| **Status** | Proposed |
| **Owner** | MintPlayer |
| **Scope** | Repo-wide: `nx.json`, `project.json` for each app & library, `@nx/dotnet` for `.csproj` projects, `.sln` coexistence, **and the library HMR fix from `PRD-NgSparkLiveRebuild.md` absorbed into this PR** |
| **Related** | `docs/PRD-NgSparkLiveRebuild.md` (earlier tactical draft — its HMR fix is now merged into this PR; that PRD will be superseded once this lands) |

> Adopt Nx as the repo's task graph across both JS **and** .NET (via `@nx/dotnet`), so the entire monorepo has a unified affected graph, caching, and dependency tracking — while keeping the existing `.sln`, `.csproj`, and ASP.NET Core hosting pattern intact. This PR also lands the library-HMR fix originally drafted in `PRD-NgSparkLiveRebuild.md` so developers see the full DX win in a single cutover.

---

## 1. Problem Statement

### 1.1 The narrow problem

Editing a file in `node_packages/ng-spark/src/**` or `node_packages/ng-spark-auth/src/**` does not hot-reload in the Fleet / HR / DemoApp / WebhooksDemo dev servers. (See `PRD-NgSparkLiveRebuild.md` §1.)

### 1.2 The broader problem

Even with the narrow fix applied, the repo has no structural understanding of its own dependency graph:

- **No "affected" awareness.** Root `package.json` runs tests via `nx run-many --target=test --exclude=@spark-demo/*,DemoApp,...,WebhooksDemo.Library` — an explicit hand-maintained exclude list. Every time a new project is added or renamed, this list drifts. `nx affected` (which runs tasks only for projects touched by a diff) is the standard Nx feature that would eliminate the maintenance tax entirely — but it requires a real Nx workspace with a real graph.
- **No cross-stack dependency tracking.** Editing `MintPlayer.Spark/Services/DatabaseAccess.cs` doesn't automatically re-run tests against Fleet's backend; editing `node_packages/ng-spark/src/foo.ts` doesn't automatically re-run Fleet's Angular tests. Developers discover breakage at PR review time.
- **No task caching.** Running `ng build` in each ClientApp is redundant if nothing changed — but MSBuild and npm scripts don't know. Nx's local/remote cache skips unchanged work.
- **Inconsistent command surface.** `dotnet build` here, `npm run build --workspace X` there, `ng serve` somewhere else. Visual Studio F5, VS Code tasks, and CLI shortcuts each take different paths to the same effective action.

### 1.3 Why adopting Nx, specifically

The repo already has `nx ^20.8.4` and `@nx-dotnet/core ^3.0.2` in `devDependencies` — the intent to adopt Nx has been planted but never completed. Nx is uniquely positioned because it has first-class support for mixed .NET + JS monorepos via `@nx/dotnet` (the official, currently-supported plugin — see §4.1 on the deprecation of `@nx-dotnet/core`).

---

## 2. Goals

1. **Single unified task graph** covering all JS apps/libs and all `.csproj` projects. `nx graph` renders the entire repo.
2. **`nx affected -t test` / `-t build` / `-t lint` works end-to-end**, replacing the hand-maintained exclude list in root `package.json:scripts.test`.
3. **Dependency-aware dev server startup**: `nx serve fleet` rebuilds `ng-spark` / `ng-spark-auth` first if they've changed, then boots the demo — no manual library builds required.
4. **Preserve the existing hosting pattern**: ASP.NET Core projects continue to spawn the Angular dev server via `MintPlayer.AspNetCore.SpaServices.UseAngularCliServer(npmScript: "start", ...)`. The `Local: https://...` regex match in `Program.cs` must keep working.
5. **Preserve Visual Studio F5**: the existing `.sln` stays as the authoritative VS load set. F5-to-debug a `.csproj` still builds & runs via MSBuild directly (not via Nx) and works as today.
6. **Preserve ng-packagr publish contract**: `npm publish` of `@mintplayer/ng-spark` and `@mintplayer/ng-spark-auth` continues to ship the same dist layout they ship today.
7. **In-place adoption, no folder reshuffle**: no moves of `Demo/**/ClientApp` or `node_packages/ng-spark*` into Nx's conventional `apps/` / `libs/` directories.

### Non-goals

- Remote caching (Nx Cloud). Mentioned in §8 Follow-ups; out of scope for this PRD.
- Replacing MSBuild or the `.sln`. Both stay as-is; `@nx/dotnet` wraps `dotnet build` / `dotnet test`.
- Enforcing "you must use `nx serve` instead of VS F5." Both paths remain valid; VS F5 just bypasses Nx's cache (the same way it bypasses the current npm-script layer).
- Migrating vitest runners to `@nx/vite:test`. Existing vitest configs are preserved; Nx invokes them via `nx:run-commands` (see §5.2). Re-evaluate later.

---

## 3. Architectural Overview

```
┌──────────────────────────────────────────────────────────────────────┐
│  Repo root                                                           │
│  ├── nx.json                         ← new: Nx workspace config      │
│  ├── MintPlayer.Spark.sln            ← unchanged, VS still opens it  │
│  ├── package.json                    ← workspaces unchanged          │
│  ├── tsconfig.base.json              ← paths tweaked (see below)     │
│  │                                                                    │
│  ├── MintPlayer.Spark/               ← has .csproj                   │
│  │   └── project.json                ← NEW (thin; @nx/dotnet wraps)  │
│  ├── MintPlayer.Spark.Abstractions/  ← has .csproj                   │
│  │   └── project.json                ← NEW                           │
│  │   … (every .csproj gets one)                                       │
│  │                                                                    │
│  ├── node_packages/ng-spark/                                         │
│  │   ├── ng-package.json             ← unchanged                     │
│  │   ├── package.json                ← unchanged                     │
│  │   └── project.json                ← NEW: ng-packagr-based build   │
│  ├── node_packages/ng-spark-auth/    ← (same shape)                  │
│  │                                                                    │
│  └── Demo/Fleet/Fleet/                                               │
│      ├── Fleet.csproj                ← unchanged                     │
│      ├── Program.cs                  ← unchanged                     │
│      ├── project.json                ← NEW: .NET project targets     │
│      └── ClientApp/                                                  │
│          ├── angular.json            ← unchanged                     │
│          ├── package.json            ← start script points at nx     │
│          └── project.json            ← NEW: app serve/build targets  │
│    … (same for DemoApp, HR, WebhooksDemo)                            │
└──────────────────────────────────────────────────────────────────────┘
```

Nothing moves. Every existing file stays where it is. `project.json` files are layered in.

### 3.1 Dev loop — dependency-aware

```
  Developer: F5 in VS, or `nx serve fleet`, or `dotnet run --project Fleet`
                               │
                               ▼
  ┌─────────────────────────────────────────────────────┐
  │ Fleet.csproj (ASP.NET Core)                         │
  │   Program.cs:                                       │
  │     spa.UseAngularCliServer(npmScript: "start", …)  │
  │                                                     │
  │   Spawns in ClientApp/:  npm run start              │
  │     └─ which now invokes: nx run fleet:serve        │
  │        (package.json scripts.start = "nx run …")    │
  └─────────────────────────────────────────────────────┘
                               │
                               ▼ (Nx evaluates graph)
  ┌─────────────────────────────────────────────────────┐
  │ fleet:serve  dependsOn: ['^build']                  │
  │   │                                                 │
  │   ├─► ng-spark:build        (ng-packagr, cached)    │
  │   ├─► ng-spark-auth:build   (ng-packagr, cached)    │
  │   │                                                 │
  │   └─► @angular/build:dev-server                     │
  │        stdout: "Local: https://localhost:XYZW"      │
  │                                                     │
  │  [stdout passes through Nx unchanged]               │
  └─────────────────────────────────────────────────────┘
                               │
                               ▼
  ┌─────────────────────────────────────────────────────┐
  │ MintPlayer.AspNetCore.SpaServices                   │
  │   regex match: "Local: https://..." → proxy target  │
  │   proxies /* to Angular dev server                  │
  └─────────────────────────────────────────────────────┘
```

Crucial detail: `nx serve` **passes child-process stdout through unchanged** (verified by `angular-researcher` — [docs](https://nx.dev/nx-api/angular/executors/dev-server)), so the `openBrowserRegex` in `Program.cs` (`Local:\s+(?<openbrowser>https?://...)`) still matches. No changes needed in `Program.cs` or `MintPlayer.AspNetCore.SpaServices`.

### 3.2 CI — affected graph

Before:
```jsonc
// root package.json
"test":          "nx run-many --target=test --exclude=@spark-demo/*,DemoApp,DemoApp.Library,...,WebhooksDemo.Library",
"test:affected": "nx affected --target=test --exclude=@spark-demo/*,..."
```

After:
```jsonc
"test":          "nx run-many --target=test",
"test:affected": "nx affected --target=test"
```

The exclude list goes away because projects it was filtering out (`DemoApp.Library`, etc.) are now first-class Nx projects with correctly-declared `test` targets (or explicitly `"test": false` in their project.json where tests don't apply).

---

## 4. Prerequisites

### 4.1 Upgrade Nx from 20.8.4 to 22.x

`@nx-dotnet/core ^3.0.2` (currently in `devDependencies`) is **deprecated**. Nx's official plugin is now `@nx/dotnet`, which ships **starting in Nx 22**. Running this PRD on Nx 20 requires either:

- **Option A (recommended)**: Upgrade Nx to the latest 22.x line first. Use `npx nx migrate latest` → `npx nx migrate --run-migrations`. Standard Nx upgrade procedure.
- **Option B**: Stay on Nx 20 and use `@nx-dotnet/core` (community plugin). Works, but on a deprecation path; migrating later adds cost. Not recommended.

Upgrading is a small, well-trodden operation (Nx migrations are heavily automated) but IS a prerequisite. The core Angular builder (`@angular/build:application`) and ng-packagr version already in the repo are compatible with Nx 22.

### 4.2 Remove `@nx-dotnet/core`

Once upgraded, remove `@nx-dotnet/core` from `devDependencies` to avoid it polluting the graph with auto-inferred `.csproj` nodes in parallel with `@nx/dotnet`.

### 4.3 Back up the `.sln`

The Nx init steps don't touch `.sln`, but a scratch branch with the `.sln` snapshotted makes rollback trivial.

---

## 5. Design

### 5.1 Workspace mode: package-based, `projectNameAndRootFormat: "as-provided"`

`nx init` offers three layouts:
- **Integrated** — projects live in `apps/` and `libs/`. Requires moving every existing folder. **Rejected** — violates goal #7.
- **Standalone** — single-app layout. Wrong fit for a multi-app monorepo.
- **Package-based** — each project keeps its own `package.json`, Nx reads the graph from them. Adding `projectNameAndRootFormat: "as-provided"` tells generators to use the caller-supplied paths verbatim instead of forcing folder conventions. **Chosen.**

`nx.json` (initial shape):
```jsonc
{
  "$schema": "./node_modules/nx/schemas/nx-schema.json",
  "workspaceLayout": {
    "projectNameAndRootFormat": "as-provided"
  },
  "targetDefaults": {
    "build":        { "cache": true, "dependsOn": ["^build"] },
    "test":         { "cache": true, "dependsOn": ["^build"], "inputs": ["default", "^production"] },
    "serve":        { "dependsOn": ["^build"] }
  },
  "namedInputs": {
    "default":    ["{projectRoot}/**/*", "sharedGlobals"],
    "production": ["default", "!{projectRoot}/**/*.spec.ts"],
    "sharedGlobals": []
  }
}
```

### 5.2 `project.json` for Angular libraries (ng-spark, ng-spark-auth)

`node_packages/ng-spark/project.json`:
```jsonc
{
  "name": "@mintplayer/ng-spark",
  "$schema": "../../node_modules/nx/schemas/project-schema.json",
  "sourceRoot": "node_packages/ng-spark/src",
  "projectType": "library",
  "targets": {
    "build": {
      "executor": "@nx/angular:package",
      "outputs": ["{projectRoot}/dist"],
      "options": {
        "project": "node_packages/ng-spark/ng-package.json"
      }
    },
    "test": {
      "executor": "nx:run-commands",
      "options": {
        "command": "vitest run --config vitest.config.ts",
        "cwd": "node_packages/ng-spark"
      }
    }
  }
}
```

`@nx/angular:package` is ng-packagr under the hood — preserves the exact same dist output. ([docs](https://nx.dev/nx-api/angular/executors/package)) Same shape for `node_packages/ng-spark-auth/project.json`.

### 5.3 Angular app rename to disambiguate from `.csproj` names

Two demo workspaces currently collide in the Nx graph with their owning `.csproj`:

| `.csproj` project name | Current workspace package name | Collision? |
|---|---|---|
| `Fleet` | `@spark-demo/fleet` | ⚠ Nx would see `Fleet` and `fleet` (Nx normalizes); rename |
| `HR` | `@spark-demo/hr` | ⚠ same; rename |
| `DemoApp` | `@spark-demo/demo-app` | OK, already distinct |
| `WebhooksDemo` | `@spark-demo/webhooks-demo` | OK, already distinct |

Rename workspace names in `package.json` (via `git mv` is unnecessary for the package.json edit itself, but any folder-move equivalents use `git mv`):
- `@spark-demo/fleet` → `@spark-demo/fleet-demo`
- `@spark-demo/hr` → `@spark-demo/hr-demo`

Update root `package.json` workspaces list (no change — paths identical), update `tsconfig.base.json` (no change — import specifiers are `@mintplayer/ng-spark*`, unaffected), and any internal cross-references if they exist (grep first). DemoApp and WebhooksDemo retain their current names.

### 5.3.1 `project.json` for Angular apps (Fleet example)

`Demo/Fleet/Fleet/ClientApp/project.json`:
```jsonc
{
  "name": "@spark-demo/fleet-demo",
  "$schema": "../../../../../node_modules/nx/schemas/project-schema.json",
  "projectType": "application",
  "sourceRoot": "Demo/Fleet/Fleet/ClientApp/src",
  "targets": {
    "build": {
      "executor": "@angular/build:application",
      "outputs": ["{projectRoot}/dist"],
      "options": { "tsConfig": "Demo/Fleet/Fleet/ClientApp/tsconfig.app.json", "browser": "src/main.ts" /* etc, copied from angular.json */ }
    },
    "serve": {
      "executor": "@angular/build:dev-server",
      "dependsOn": ["^build"]
    },
    "test": {
      "executor": "nx:run-commands",
      "options": { "command": "vitest run", "cwd": "Demo/Fleet/Fleet/ClientApp" }
    }
  }
}
```

**Full conversion from `angular.json` to `project.json`** (no `extends` alternative). Execute the conversion with:
```bash
git mv Demo/Fleet/Fleet/ClientApp/angular.json Demo/Fleet/Fleet/ClientApp/project.json
```
then edit the renamed file to conform to Nx's `project.json` schema (strip the outer `$schema`/`projects` wrapping, promote the single project's body, rename `architect` → `targets`, flatten builder options). Repeat for each of the four ClientApps. `git mv` preserves file history.

### 5.4 Library HMR — in-scope for this PR

Nx's `dependsOn: ['^build']` on the `serve` target ensures libraries are **built** before serve starts, but a one-shot build doesn't solve the steady-state HMR problem: once the Vite dev server is running, it pre-bundles library modules and does not re-read them when dist changes. We need **library watch mode + a consumption path that bypasses Vite pre-bundling**.

Chosen approach (ng-packagr watch + dist-mapped tsconfig paths, adapted from `PRD-NgSparkLiveRebuild.md` §3.1 Approach A):

1. **Flip `tsconfig.base.json` paths** for both libraries from source to dist:
   ```jsonc
   "paths": {
     "@mintplayer/ng-spark":        ["node_packages/ng-spark/dist"],
     "@mintplayer/ng-spark/*":      ["node_packages/ng-spark/dist/*"],
     "@mintplayer/ng-spark-auth":   ["node_packages/ng-spark-auth/dist"],
     "@mintplayer/ng-spark-auth/*": ["node_packages/ng-spark-auth/dist/*"]
   }
   ```

2. **Add a `watch` target to each library's `project.json`**:
   ```jsonc
   "watch": {
     "executor": "nx:run-commands",
     "options": { "command": "ng-packagr -p ng-package.json --watch", "cwd": "node_packages/ng-spark" }
   }
   ```

3. **Add a `dev` target to each demo's `project.json`** that fans out the two library watchers + the serve concurrently:
   ```jsonc
   "dev": {
     "executor": "nx:run-commands",
     "options": {
       "parallel": true,
       "commands": [
         "nx watch @mintplayer/ng-spark",
         "nx watch @mintplayer/ng-spark-auth",
         "nx serve @spark-demo/fleet-demo"
       ]
     },
     "dependsOn": ["@mintplayer/ng-spark:build", "@mintplayer/ng-spark-auth:build"]
   }
   ```
   `dependsOn` primes both dists once before the parallel watchers spin up, guaranteeing the serve target has something to import against on cold boot.

4. **Change `package.json:scripts.start` in each ClientApp**:
   ```jsonc
   "start": "nx run @spark-demo/fleet-demo:dev"
   ```
   This keeps `MintPlayer.AspNetCore.SpaServices.UseAngularCliServer(npmScript: "start", ...)` working without any `Program.cs` change. The `Local: https://...` line written by `@angular/build:dev-server` passes through Nx's parallel runner to stdout (verified by `angular-researcher` against [`@nx/angular` dev-server executor docs](https://nx.dev/nx-api/angular/executors/dev-server)).

5. **Why this solves HMR**: with the tsconfig path pointing at `dist/` (not `src/`), Vite resolves library imports to the dist output via the npm-workspaces symlink. ng-packagr's watch rebuilds dist on every library source change; Vite's file watcher DOES observe dist file writes (they're downstream of the pre-bundle step, not inside it) and invalidates the module graph. The steady-state loop is: edit source → ng-packagr rebuilds dist (~0.5-2s) → Vite sees dist change → browser reloads.

6. **Open source-map path for library debugging**: `ng-packagr`'s `--watch` output includes source maps pointing back at `node_packages/ng-spark/src/...`, so breakpoints in library source still hit in the browser. No degradation vs. the status-quo source-path mapping.

### 5.5 `project.json` for .NET apps — `@nx/dotnet`

`@nx/dotnet` auto-infers targets for each `.csproj` by walking the `.sln` or by scanning for `*.csproj`. The generated `project.json` is thin — typically:

```jsonc
{
  "name": "Fleet",
  "$schema": "../../../../node_modules/nx/schemas/project-schema.json",
  "projectType": "application",
  "sourceRoot": "Demo/Fleet/Fleet",
  "implicitDependencies": ["@spark-demo/fleet-demo"],
  "tags": ["scope:demo", "type:api"]
}
```

The `implicitDependencies: ["@spark-demo/fleet-demo"]` line is the critical wire: it tells Nx that when the Angular app changes, the .NET app is affected — so `nx affected -t build --projects=Fleet` will rebuild both. Inverse direction (the Angular app depending on the `.csproj`) is usually NOT declared because the Angular app is decoupled from the server at build time.

### 5.6 `tsconfig.base.json` paths — flipped to dist as part of HMR fix

See §5.4 step 1. The source-to-dist flip is necessary for HMR (not for Nx per se) and is captured in that section to keep the HMR design contiguous.

---

## 6. Implementation Plan — phased

### Phase 1 — Nx upgrade + `nx init` (1 day)

1. `npx nx migrate latest` → apply migrations → commit.
2. Remove `@nx-dotnet/core` from `package.json`.
3. `nx init` with package-based mode. Produces `nx.json` with `projectNameAndRootFormat: "as-provided"`.
4. Verify no existing scripts broke (`npm test` still functions).

### Phase 2 — Workspace-name rename (0.5 day)

1. Rename workspace packages to avoid `.csproj` clashes:
   - `@spark-demo/fleet` → `@spark-demo/fleet-demo` (edit `Demo/Fleet/Fleet/ClientApp/package.json`)
   - `@spark-demo/hr` → `@spark-demo/hr-demo` (edit `Demo/HR/HR/ClientApp/package.json`)
2. `git grep` for old names; update any internal references (root scripts, test config, CI).
3. Re-run `npm install` to refresh the symlink set.

### Phase 3 — Angular libraries + HMR wiring (1-1.5 days)

1. Add `project.json` to `node_packages/ng-spark` and `node_packages/ng-spark-auth` with `build` (`@nx/angular:package`), `watch` (ng-packagr `--watch` via `nx:run-commands`), and `test` (vitest via `nx:run-commands`) targets.
2. **Flip `tsconfig.base.json` paths** for both libraries from source to dist (see §5.4 step 1).
3. Prime dists: `nx run-many -t build --projects=@mintplayer/ng-spark,@mintplayer/ng-spark-auth`.
4. Verify `nx build @mintplayer/ng-spark` produces the same dist layout as `npm run build --workspace @mintplayer/ng-spark` did pre-migration. Diff the outputs.
5. Verify `nx test @mintplayer/ng-spark` runs the same vitest suite with the same results.

### Phase 4 — Angular apps (convert angular.json → project.json via `git mv`) (1-2 days)

For each of Fleet / HR / DemoApp / WebhooksDemo:

1. `git mv Demo/<App>/<App>/ClientApp/angular.json Demo/<App>/<App>/ClientApp/project.json` — preserves file history.
2. Edit the renamed file to conform to Nx's `project.json` schema:
   - Strip the outer `$schema` / `version` / `projects` / `newProjectRoot` / `cli` envelope.
   - Promote the single project's body to the top level.
   - Add `"name": "@spark-demo/<name>-demo"` (or existing name for non-renamed apps).
   - Rename `architect` → `targets`.
   - Copy `build` / `serve` / `test` target bodies verbatim.
3. Add a `dev` target orchestrating both library watchers + serve (see §5.4 step 3).
4. Flip `package.json:scripts.start` from `ng serve` to `nx run <project>:dev` (see §5.4 step 4).
5. Start each `.NET` project via F5 or `dotnet run`. Confirm:
   - SpaServices spawns the dev target correctly.
   - The `Local: https://...` regex still matches — ASP.NET proxies to the dev server.
   - Editing a `.ts` file in `node_packages/ng-spark/src/` updates the browser within ~5 seconds.
6. Run `nx graph` — confirm library nodes appear as dependencies of each app node.

### Phase 5 — `@nx/dotnet` for .NET projects (2-3 days)

1. `nx add @nx/dotnet`.
2. Let it auto-infer `.csproj` projects. Review each generated `project.json` for correctness.
3. For each demo `.csproj`, add `implicitDependencies` pointing at the renamed ClientApp name (e.g., `Fleet.csproj`'s project.json → `implicitDependencies: ["@spark-demo/fleet-demo"]`).
4. Verify `nx affected -t build` with a single `.cs` edit correctly rebuilds the owning `.csproj` and leaves others cached.
5. Verify VS F5 on `Fleet.csproj` still builds & debugs. (Expected: yes — F5 uses MSBuild, which Nx doesn't intercept.)
6. If anything misbehaves on Windows (long paths, concurrent `dotnet` invocations), document it and escalate — the team has pre-agreed to proceed on Windows as-is and triage issues as they surface.

### Phase 6 — CI migration with last-green base ref (1 day)

1. Replace `test` / `test:affected` scripts in root `package.json` with the plain `nx run-many` / `nx affected` forms (no more `--exclude=` list).
2. Wire last-green base ref in GitHub Actions via [`nrwl/nx-set-shas`](https://github.com/nrwl/nx-set-shas), which sets `NX_BASE` / `NX_HEAD` to the last successful workflow SHA. `nx affected` without an explicit `--base` then uses those env vars.
3. Monitor the first few PRs for false-negatives (affected graph missing a dependency) and adjust `implicitDependencies` / `namedInputs` accordingly.

### Phase 7 — Documentation (0.5 day)

1. Root `README.md` or `CONTRIBUTING.md`: "Use `npm run start` inside a ClientApp (which now delegates to Nx), F5 in VS for full backend+frontend, or `nx affected -t test` locally before pushing."
2. Memory entry capturing the new workflow and the `@spark-demo/*-demo` rename.

**Total estimate: ~8-11 dev-days across one or two engineers. Medium risk (driven by Phase 5's `@nx/dotnet` Windows maturity and the angular.json→project.json conversion).**

---

## 7. Acceptance Criteria

1. `nx graph` renders every `.csproj` project + every ClientApp + both libraries as a connected graph. ng-spark and ng-spark-auth show as dependencies of all four demo ClientApps.
2. F5 on any demo `.csproj` (or `dotnet run`) boots the demo end-to-end. ASP.NET Core proxies requests correctly. The `openBrowserRegex` in `Program.cs` matches without modification.
3. **HMR works end-to-end**: editing a `.ts` / `.html` / `.scss` under `node_packages/ng-spark/src/` **or** `node_packages/ng-spark-auth/src/` while a demo runs → browser reflects the change within 5 seconds. Verified in Fleet and HR at minimum, with library breakpoints still hitting in browser devtools (source maps functional).
4. Editing a `.cs` file in `MintPlayer.Spark/` causes `nx affected -t build` to rebuild `MintPlayer.Spark` + dependent `.csproj`s, skipping unrelated projects.
5. `nx affected -t test` with a change in `node_packages/ng-spark/src/` runs only ng-spark tests (and any declared-dependent test suites), not the whole suite.
6. VS F5 on any demo `.csproj` launches and debugs as today. No warnings about Nx.
7. `npm publish` output for ng-spark / ng-spark-auth is byte-for-byte comparable with the pre-migration dist.
8. Root `package.json` no longer contains the hand-maintained `--exclude=` list.
9. CI uses last-green base ref via `nrwl/nx-set-shas`; typical PRs skip unaffected test suites.

---

## 8. Follow-Ups (Out of Scope)

- **Nx Cloud** — remote cache + distributed task execution. Material CI speedup; free tier sufficient for this size repo. Add post-landing.
- **ESLint unified via Nx** — `@nx/eslint:lint` executor across all projects.
- **Per-project `eslint` / `prettier` configs** inheriting from root defaults.
- **Module boundary rules** (`@nx/enforce-module-boundaries`) — prevent demo apps from importing each other accidentally.
- **`@nx/storybook` for ng-spark / ng-spark-auth** component catalog.

---

## 9. Resolved Decisions (from initial review)

| | Decision | Reflected in |
|---|---|---|
| Nx upgrade | Upgrade to Nx 22 as Phase 1 prerequisite; proceed and triage surface-area issues as they surface | §4.1, §6 Phase 1 |
| `angular.json` → `project.json` | Full conversion via `git mv` (history preserved) | §5.3.1, §6 Phase 4 |
| `@nx/dotnet` on Windows | Proceed as-is; address issues as encountered | §6 Phase 5 step 6 |
| Workspace name collisions | Rename clashing ClientApps to `-demo` suffix: `@spark-demo/fleet-demo`, `@spark-demo/hr-demo` | §5.3, §6 Phase 2 |
| Vitest runner | Keep vitest configs untouched; invoke via `nx:run-commands`. Do NOT adopt `@nx/vite:test` | §5.2, Non-goals |
| CI base ref | Last-green via `nrwl/nx-set-shas` | §6 Phase 6 |
| HMR scope | In-scope for this PR (ng-packagr watch + dist-mapped tsconfig paths + orchestrated `dev` target) | §5.4, §6 Phase 3-4 |

## 9.1 Still Open

1. **Precise output diff of `@nx/angular:package` vs. current `ng-packagr` build.** Should be byte-equivalent; Phase 3 step 4 makes this an explicit verification step, but if a diff surfaces, we need a decision on whether to adjust or accept.
2. **`nx watch` granularity for ng-packagr output.** If `ng-packagr --watch` writes dist updates in multiple passes per edit, Vite may see partial state and flash errors. Mitigation: ng-packagr's incremental writer is generally atomic-per-file, but worth smoke-testing. Fallback is `concurrently` driven from a root script (less elegant but proven — see `PRD-NgSparkLiveRebuild.md` §3.1).
3. **Does `nx run-commands --parallel` on Windows handle Ctrl+C cleanly with 3 children?** Tested alternative: wrap in `concurrently` via `run-commands` `command:` with the `--kill-others` flag.

---

## 10. Relationship to `PRD-NgSparkLiveRebuild.md`

`PRD-NgSparkLiveRebuild.md` was drafted first as a tactical fix for the HMR pain. This PRD now absorbs its entire solution (ng-packagr watch + dist-mapped tsconfig paths) into Phase 3-4 of the combined rollout. Once this PRD lands:

- `PRD-NgSparkLiveRebuild.md` is **superseded** — mark it Status: Superseded, linking here.
- The HMR win and the Nx adoption ship in a single PR.
- Developers don't experience an intermediate state where HMR works via one mechanism and is then re-plumbed for Nx.

**Delivered in this PR:**
- Unified Nx task graph (JS + .NET via `@nx/dotnet`)
- `nx affected` + caching + last-green CI
- Workspace package renames to eliminate `.csproj` name collisions
- `angular.json` → `project.json` conversion with history preserved via `git mv`
- Library HMR (edit `ng-spark(-auth)` source → browser updates in seconds)
- `.sln` and `Program.cs` both untouched; VS F5 unchanged

**Estimated effort**: ~8-11 dev-days. Medium risk, driven by Nx 22 upgrade quirks and `@nx/dotnet` Windows behavior.

---

## 11. Rollback

Every phase is independently revertable:
- **Phases 1-3** revert: `git revert` of the Nx-init and project.json adds. Root scripts restored. No runtime impact.
- **Phase 4** revert: remove `.NET project.json` files and `@nx/dotnet` dep. `.sln` is untouched throughout, so VS keeps working.
- **Phase 5** revert: restore the `--exclude=` list in root `package.json`.

No published package or runtime behavior changes — rollback is a config-only operation.
