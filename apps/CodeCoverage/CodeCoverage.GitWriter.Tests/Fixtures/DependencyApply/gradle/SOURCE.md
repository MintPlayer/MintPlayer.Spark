# Source: S13 apply spike, gradle

Generated 2026-10-15 by `spikes/s13/run-gradle.sh (gradle:8.10-jdk21)` (sample repo built under `spikes/s13/work/`, one base commit, one pinned bump per case).
Each `*.diff` is `git diff` (untracked files included via `git add -N`) after the case's command, against the base commit.
Timings: `spikes/s13/logs/_timings.txt`; raw tool output: `spikes/s13/logs/<case>.log`.

Version catalog + dependencyLocking.lockAllConfigurations().
- textedit-catalog-write-locks: text edit libs.versions.toml + `gradle dependencies --write-locks` - pure + gradle.lockfile.
- textedit-versionref-update-locks: [versions] ref + `gradle dependencies --update-locks group:name`.
- Every Gradle invocation EVALUATES settings.gradle.kts/build.gradle.kts = repo code runs (criterion 2).
- conflict-missing-version.log: prose.
