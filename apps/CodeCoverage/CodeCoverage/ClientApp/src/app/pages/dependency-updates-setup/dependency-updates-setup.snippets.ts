/*
 * The code blocks of the dependency updater's setup page (/dependency-updates/setup), apart from the component so that
 * each one reads as the file it shows.
 *
 * ⚠️ DEPENDENCIES_YML is a copy: it is the `yaml` block of docs/code-coverage/dependency-updates.md §4, byte for byte,
 * without its final newline. dependency-updates-setup.snippets.spec.ts reads the guide from disk and fails when either
 * side changes without the other. The keys it shows are the ones DependenciesYml.cs parses.
 */

/** Every key of the `dependencies:` section, with an example value (docs/code-coverage/dependency-updates.md §4). */
export const DEPENDENCIES_YML = `dependencies:
  enabled: false                 # opt this repository out (opting in is the setting)
  schedule: { frequency: weekly, weekday: monday, hour: 6 }   # daily | weekly | monthly
  cooldown: 7d                   # may only lengthen the account's cooldown
  autoMerge:
    default: minor               # never | patch | minor | major   (built-in: minor)
    "@angular/*": never          # the most specific glob wins
    maxCoverageDrop: 0.5         # percentage points; built-in 0
  ignore: ["Microsoft.CodeAnalysis*"]
  rules:
    - match: ["vitest", "@vitest/*"]
      ecosystem: npm             # optional
      group: vitest              # move together
      allowMajor: true           # plan majors without an approval (they still never auto-merge)
      hold: "waiting for the Nx plugin"   # or true; held until removed
      lockstep: true             # the group moves only to a version every member has
  follow:                        # pins that are not dependencies, written with a dependency's new version
    - dependency: mcr.microsoft.com/playwright
      path: apps/web/docker-compose.yml   # a glob
      match: 'web-chromium:(?<version>[0-9.]+)'
      version: '^v(?<version>[0-9.]+)-noble$'   # optional, over the dependency's new value
  exclude: ["samples/**"]        # package files left alone
  lookupAction: dependency-lookup.yml
  applyAction: dependency-apply.yml
  migrate:
    - match: "@angular/core"
      onMajor: angular-migrate.yml
  platforms:
    dotnet:
      track: stable
      strategy: replace
      keepPrevious: 0
      lockPackageMajor: false
      locations: []
      exclude: []
      companions:                # packages tied to the SDK, first match wins; leave it out for the built-in list
        - match: "Microsoft.CodeAnalysis*"            # a source generator that must load on SDK 10.0.1xx
          versions: { "10.0": "<=5.0", "11.0.1xx": "5.12.x" }   # channel or SDK band → version range
        - match: "Microsoft.Build*"
          bundled: msbuild       # at or below what the repository's SDK ships (msbuild | roslyn | nuget)
        - builtin                # the built-in list (Microsoft.AspNetCore.*, Microsoft.Extensions.*, …); [] turns them off
    node: { track: lts, companions: ["@types/node"] }   # a plain glob: the package takes the SDK's major
    # autoMerge: major           # opt in: this SDK's majors auto-merge like any update (§6); the only value
  lockFileMaintenance: false     # transitive-only refreshes, off by default
  registries: ["npm.pkg.github.com"]   # lockfile hosts allowed besides the public registries
  lines: patch                   # release lines: patch (default) | all | security | off`;

/** The least a repository needs in `coverage.yml` before the dependency pull request may auto-merge (R5). */
export const MERGE_QUEUE_ON = `mergeQueue:
  enabled: true`;
