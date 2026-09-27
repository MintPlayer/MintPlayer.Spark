# Combined dependency bumps — plan

One PR replacing every open single-package bump PR (Dependabot + #412), based on master `bd217fcb`.
Rule: take the **latest stable** version where one is newer than the PR's target; keep a preview only
where master already pins one and that line has no stable release. Platform majors stay locked
(npm = Angular 22, NuGet libs = 11.x).

## NuGet

| Package | master | → | Files | PRs |
|---|---|---|---|---|
| Microsoft.NET.Test.Sdk | 18.9.0 / 18.0.1 | 18.10.1 | 4 `tests/*` + CodeCoverage.Tests | #456 #450 #430 #412 |
| MintPlayer.AspNetCore.SpaServices | 10.5.0 | 10.7.1 | 4 app csprojs | #446–#449 #407 #412 |
| RavenDB.TestDriver | 7.2.1 | 7.2.6 | CodeCoverage.Tests | #410 #406 #412 |
| coverlet.collector | 6.0.4 | 10.0.1 | CodeCoverage.Tests (others already 10.0.1) | #170 #412 |
| xunit.runner.visualstudio | 3.1.5 | 4.0.0 | CodeCoverage.Tests (others already 4.0.0) | #412 |
| YamlDotNet | 16.3.0 | 18.1.0 | CodeCoverage app | #412 |
| Octokit.Webhooks.AspNetCore | 4.1.2 | 4.2.0 | libs/webhooks/…Webhooks.GitHub (+ own Version preview.90 → .91) | #412 |

Not taken:
- **Microsoft.CodeAnalysis.\*** — already 5.9.0. The runtime-lib `PackageReference`s that #430/#450/#456 add are
  skipped: they would make every NuGet consumer depend on Roslyn.
- **MintPlayer.Assertions 1.1.0** (#408), **Endpoints 11.0.0** (#332), and **ASP.NET 10.0.12** (#412): each is a
  downgrade against master's 11.x previews.
- **Verify.Xunit** (#203) and **NCrontab** (#177): already applied. The 4 test projects in **coverlet** (#170) are
  already applied too.
- **FluentAssertions 8** (#171): excluded because of its licence. The repo uses MintPlayer.Assertions.

## npm — root workspace

| Package(s) | master | → | PRs |
|---|---|---|---|
| all `@angular/*`, `@angular-devkit/*`, `@schematics/angular` (deps + overrides) | 22.1.3 / 22.1.5 | 22.2.0 | #454 (it missed the devkit/schematics overrides) |
| ng-packagr (dev + override) | 22.1.1 | 22.2.1 | — (keeps the toolchain aligned) |
| nx, @nx/angular, @nx/dotnet | 23.1.1 | 23.2.1 | #341 |
| @analogjs/vite-plugin-angular, @analogjs/vitest-angular | ^2.7.0 | ^2.7.5 | #346 #344 |
| @mintplayer/ng-click-outside, ng-focus-on-load | ^22.0.0 | ^22.1.0 | #364 #345 |
| @mintplayer/pagination | ^2.3.0 | ^2.5.0 | #334 |
| vitest + @vitest/coverage-v8 (root + action) | 4.1.11 | 5.0.2 | #371 (it was broken: coverage-v8 5 has an exact peer on vitest 5) |
| sass-embedded (new explicit devDependency) | hoisted transitively | ^1.105.0 | — |

`sass-embedded`: `@angular-devkit/build-angular` 22.2 dropped its sass dependencies, and those were what hoisted
`sass-embedded` to the root. `@angular/build` (1.104.1) and `ng-packagr` (1.105.0) now each nest their own copy, so
the ng-spark vitest suite, which compiles component SCSS, failed 7 files with "Preprocessor dependency sass-embedded
not found". It is a real dependency of that suite, so it is now declared.

Do not run `nx migrate`: @nx/angular 23.2's package update would pin Angular to ~22.1. The only nx migration
(executor-keyed targetDefaults) is a no-op for our nx.json.

## npm — coverage-upload action (`apps/CodeCoverage/action`, production)

- `@actions/core` 1.11.1 → ^3.0.1 (#373) and `@actions/github` 6.0.1 → ^9.1.1 (#372). Both are ESM-only.
  `@actions/exec` → ^3.0.0 and `@actions/glob` → ^0.7.0 go with them, so the bundle doesn't carry a second copy
  of core 1.x.
- tsconfig: `module: esnext` and `moduleResolution: bundler`, so ncc resolves the `import` condition. The
  output stays a CJS `dist/index.js`.
- Tests:
  - Replace `vi.importActual('@actions/github/lib/context')`, which is no longer exported, with
    `context.constructor`.
  - Replace `vi.spyOn(core, …)` on the ESM namespace with `vi.mock('@actions/core', { spy: true })`.
- Rebuild and commit `dist/index.js`. CI's verify mode fails on any difference.
- vitest 5 requires Node ≥22, so move `node-version` from 20.x to 22.x in `pull-request.yml` and
  `coverage-action-publish.yml`.

## Verification (batched at the end)

1. Root `npm install`: the lockfile is regenerated, not merged from the PRs.
2. Action: `npm test`, `npm run test:coverage`, `npm run build`, `npm run test:bundle`.
3. `npx nx run-many -t test` for the Angular libs, and `ng-spark` build.
4. `dotnet build` of the `.slnx`, then all 5 test projects, including CodeCoverage.Tests.
5. CI: in the PR's self-upload step, confirm the coverage was actually uploaded, not just that the step went green.

## After merge
Close the superseded PRs: #456 #454 #450 #449 #448 #447 #446 #430 #412 #410 #408 #407 #406 #373 #372 #371
#364 #346 #345 #344 #341 #334 #332 #203 #177 #171 #170.
