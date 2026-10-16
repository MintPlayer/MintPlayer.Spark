# Source: dependency-update contract fixtures

Shared by the runner (`apps/CodeCoverage/dependency-action`, ajv) and the bot (C#, JsonSchema.Net + DTO
round trip). The file-name prefix names the schema: `lookup.*` -> `record.schema.json`, `plan.*` ->
`plan.schema.json`, `result.*` -> `result.schema.json`, `platform.*` -> `platform-record.schema.json`,
`migrate-result.*` -> `migrate-result.schema.json`. Every `valid/` file must validate; every `invalid/`
file must be refused.

Generated 2026-10-16 (milestone N3) by a one-off script over the action's own code (scratchpad
`gen/gen-contract-fixtures.ts`, bundled with esbuild), from REAL spike outputs:

| File | Derived from |
|---|---|
| `valid/lookup.{npm,nuget,docker-actions-platform,maven,go}.json` | `mapReport()` over the S12 Renovate 44.125.2 reports (`dependency-action/test/fixtures/renovate/<sample>/report.json`); platform records from the S12 sample input trees + the captured feeds (`test/fixtures/platform-feeds`); interface diffs from the S12 `action.yml` pairs against the sample's `ci.yml`. `expectedSha` = the commit S12 captured this repo at |
| `valid/platform.s19-{dotnet,node}.json` | `platformRecords()` over the S19 golden tree (this repo before 9808c1a3b, the net11 retarget) + the captured feeds |
| `valid/plan.npm-three-groups.json`, `valid/result.npm-three-groups.json` | a REAL apply run (`runApply` + real npm 11 against registry.npmjs.org) on the S13 npm sample with react/react-dom added: express ^4.18.2 -> ^4.19.2 (applied), react-dom 17 -> 18 (unresolvable, ERESOLVE, blocker `react`), chalk 4.1.0 -> 4.1.2 (applied). The patch is `dependency-action/test/fixtures/apply-results/npm-three-groups.patch`. Local paths scrubbed to `<HOME>` |
| `valid/plan.platform-dotnet.json` | the platform plan for the S19 tree (net10 -> net11, sdk `11.0.100-rc.1.26425.128` as in the commit's global.json, `lockPackageMajor`) |
| `valid/migrate-result.nx-22-to-23.json` | the S17 Nx 22.7.12 -> 23.3.0 run: its step timings, its typescript `~5.9.2` -> `6.0.3` extra bump (R19), and the byte size + sha256 of its real patch (`spikes/s17/fixtures/migrate/nx-22-to-23.patch`) |
| `invalid/*.json` | one targeted mutation of a valid file each (the name says which) |
