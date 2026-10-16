# Source: Renovate lookup reports (S12)

Byte-for-byte copies of `apps/CodeCoverage/dependency-action/test/fixtures/renovate/<sample>/report.json` and its
`SOURCE.md` (tool, image digest, command, capture date), copied 2026-10-16 for milestone N4 of
`docs/code-coverage/dependency-updates-PRD.md`. The `input/` trees are not copied: the bot never reads a manifest (R1).

They are frozen captures: registry answers change daily, so no test may expect them to match a fresh run. When the runner
re-captures them (`tools/capture-fixtures.mjs`), copy them again and re-derive the expectations in
`Dependencies/Policy/SelectorReplayTests.cs` from the new `mintplayer-spark/report.json`.

Used by:
- `LookupReportParserTests`: the C# port of the runner's `mapReport()` (`RenovateReportMapper`) must give the records the
  runner's own contract fixtures (`contracts/dependency-update/fixtures/valid/lookup.*.json`) hold for the same reports.
- `SelectorReplayTests`: the policy engine on this repository's real report (504 dependencies, 69 with updates).
