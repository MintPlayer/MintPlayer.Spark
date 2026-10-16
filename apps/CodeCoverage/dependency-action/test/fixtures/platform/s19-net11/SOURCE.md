# Source: S19 golden (dotnet platform adapter)

- Repository: MintPlayer/MintPlayer.Spark, commit `9808c1a3b2145652b1e248f9ccf528920a221442`
  "Retarget the whole solution to .NET 11" (2026-09-24), the hand-made net10 -> net11 migration.
- `before/<path>`: `git show 9808c1a3b^:<path>` for every file that commit touched, except `CLAUDE.md` (prose).
- `commit.diff`: `git diff --no-color 9808c1a3b^ 9808c1a3b -- <those files>`.
- Captured 2026-10-16 for milestone N3 (scratchpad `capture-s19.mjs`).

Differences between the commit and the platform adapter's dry run are expected and asserted by category
in `applyPlatform.test.ts`:
- `PackageReference` version bumps (Microsoft.* 10.0.11 -> 11.0.0-rc.1...): locked packages, planned as
  NuGet items next to the platform group, not platform edits (R7).
- Removed `PackageReference` lines (Cron, Migrations, ...): a human cleanup in the same commit.
- Workflow comments mentioning net10.0/net11.0: prose, never edited.
