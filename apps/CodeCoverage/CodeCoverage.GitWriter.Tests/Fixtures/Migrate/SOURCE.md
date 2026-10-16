# fixtures/migrate — provenance (spike S17, 2026-10-15)

All produced locally on Windows 11, Node 24.15.0, npm 11.12.1, git 2.47.1, public npm registry.
Local paths in logs are scrubbed to `<WORKDIR>`, `<TEMP>`, `<HOME>`; ANSI colours stripped.

## Angular (scratch repo `ng-app`, `@angular/cli@21 new --minimal --skip-install`)

Baseline: Angular 21.2.x, plus `src/app/user-card.ts` (optional chaining in template, `@Input`) and a
`.github/workflows/ci.yml`; `strictTemplates` removed from `tsconfig.json` (an "older app" shape).
Apply (simulated): text-edit `@angular/*` `^21.2.x` -> `^22.2.3`, `typescript ~5.9.2` -> `~6.0.0`
(`angular-apply-package.json`), then `npm install --package-lock-only --ignore-scripts`, commit.
Migrate: `npm ci --ignore-scripts`, then
`NG_DISABLE_VERSION_CHECK=1 NG_CLI_ANALYTICS=false npx --no-install ng update <pkg> --migrate-only --from=<old> --to=<new>`.

| File | What |
|---|---|
| `angular-core-21-to-22.patch` / `.log` | `@angular/core --from=21.2.0 --to=22.2.3`: exit 0, 38 s. Touches `src/app/app.ts`, `src/app/user-card.ts`, `tsconfig.app.json` |
| `angular-cli-karma-21-to-22.patch` / `.log` | `@angular/cli --from=21.2.27 --to=22.2.3` on a variant with a `@angular/build:karma` test target: edits `package.json` (adds `istanbul-lib-instrument`) and the schematic itself ran `npm install --ignore-scripts`, so `package-lock.json` changed too. Without Karma the CLI migrations made no changes |
| `angular-core-partial-EPERM.patch` / `.log` | Failure mid-run: `tsconfig.app.json` made read-only. Exit 1; migrations 1-6 already written (`app.ts`, `user-card.ts`), the failing one and the rest not run |

## Nx (scratch repo `nx-ws`, hand-written; `create-nx-workspace@22.7.12` ignored the version and produced 23.2.0)

Baseline: `nx` + `@nx/js` 22.7.12, typescript ~5.9.2, one `@nx/js:tsc` lib, `nx.json` with legacy
`releaseTagPattern`, `tsconfig.base.json` with `baseUrl`/`moduleResolution: node`, `tools/read-config.ts`
importing `@nx/js/src/...` and `createNodesV2`. Apply: `22.7.12` -> `23.3.0`, lock with `--ignore-scripts`, commit.
Migrate: `npm ci --ignore-scripts`; `npx nx migrate 23.3.0 --from=nx@22.7.12,@nx/js@22.7.12`; `npm install --ignore-scripts`;
`npx nx migrate --run-migrations` (env `NX_DAEMON=false NX_NO_CLOUD=true`).

| File | What |
|---|---|
| `nx-22-to-23.migrations.json` | 9 migrations generated with default `--include` (all) |
| `nx-22-to-23.required.migrations.json` | 7 migrations with `--include=required` (the 2 TS6 tsconfig migrations drop out together with the optional `typescript` bump) |
| `nx-22-to-23.patch` / `.stat.txt` / `.run-migrations.log` | Full run (include=all), exit 0. Touches `.gitignore`, `nx.json`, `tsconfig.base.json`, `libs/util/tsconfig.lib.json`, `tools/read-config.ts`, `package.json` (typescript `~5.9.2` -> `6.0.3`), `package-lock.json`, plus new `migrations.json` |
| `nx-partial-EPERM.patch` / `.log` | Failure mid-run (`.gitignore` read-only): exit 1, "Run halted at migration 3 of 7. 2 applied. 4 not attempted."; `nx.json` and `tools/read-config.ts` already edited |

Raw logs (unscrubbed) live in `../../logs/`; the scratch repos are `../../ng-app` and `../../nx-ws` (branch `migrated`).
