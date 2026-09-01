# MintPlayer.Spark — repository instructions

## Running the demo apps: never start the Angular dev server yourself

Every demo app (`apps/DemoApp`, `apps/Fleet`, `apps/HR`, `apps/WebhooksDemo`) hosts its SPA through
**`UseAngularCliServer`** — the ASP.NET Core host spawns `npm start` itself and proxies it. So:

- **`dotnet run` is the whole command.** Do not run `ng serve` / `npm start` alongside it; a second
  dev server just fights for ports.
- **Do not run `ng build` / `ng test` against that workspace while the host is running.** It is
  unnecessary (the host builds), it shares `.angular/cache` with the live dev server, and it can
  wedge the file watcher.
- **To see a client change:** save the file. The dev server rebuilds and the browser live-reloads.
- **To see a *server* change:** restart the host — watch mode does not reload C#. Kill the whole
  `dotnet run` process **tree**, not just the child `<App>.exe`, or the next build fails with
  MSB3027/MSB3021 "file is locked by <App>".
- **If output looks stale**, suspect a wedged watcher rather than your code, and restart the host.

The host prints the dev server's own port (`➜ Local: http://localhost:NNNNN/`) once it is ready;
that is the signal the app is actually serviceable, not `Now listening on:`.

## Versioning: major version is locked to the targeted platform

The major version of every published package in this repository is **not** a semver
signal we are free to bump. It states which platform the package targets, and it moves
only when that platform moves.

### npm packages (`@mintplayer/ng-spark`, `@mintplayer/ng-spark-auth`, …)

The major version **must equal the major Angular version the package is compatible with**.

- Compatible with Angular 22 → `22.x.x`
- Compatible with Angular 23 → `23.x.x`

A breaking change in our own API is **not** a reason to bump the major. Ship it as a
minor (`22.2.0` → `22.3.0`) and describe the break in the release notes. The major is
reserved for the Angular upgrade that actually makes the package require the new
framework version.

All npm packages in the workspace move their major **together**, in the same PR as the
Angular upgrade itself.

### NuGet packages (`MintPlayer.Spark*`)

The major version **must equal the major .NET version the package targets**.

- `net10.0` → `10.x.x` (currently `10.0.0-preview.*`)
- `net11.0` → `11.x.x`

Same rule as above: an API break inside a .NET generation is a minor bump, never a
major one.

### Before bumping a version

Ask: *did the targeted Angular / .NET major change?* If the answer is no, the major
digit stays exactly where it is. Getting this wrong is expensive — a wrongly published
major cannot be reused on npm even after it is unpublished or deprecated, so the real
`23.0.0` (or `11.0.0`) is burned forever.

CI publishes on push to `master`, so a wrong version number in `package.json` /
`.csproj` becomes a permanent public artifact as soon as the PR merges. Check the
version diff in the PR review.
