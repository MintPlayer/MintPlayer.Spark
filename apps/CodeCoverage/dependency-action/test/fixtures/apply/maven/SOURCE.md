# Source: S13 apply spike, maven

Generated 2026-10-15 by `spikes/s13/run-maven.sh (maven:3.9-eclipse-temurin-21, versions-maven-plugin 2.17.1)` (sample repo built under `spikes/s13/work/`, one base commit, one pinned bump per case).
Each `*.diff` is `git diff` (untracked files included via `git add -N`) after the case's command, against the base commit.
Timings: `spikes/s13/logs/_timings.txt`; raw tool output: `spikes/s13/logs/<case>.log`.

No lockfile.
- native-set-property: `versions:set-property -Dproperty=jackson.version -DnewVersion=2.16.1` - pure (comment kept).
- native-use-dep-version: `versions:use-dep-version -Dincludes=g:a -DdepVersion=... -DforceVersion=true` - pure.
- textedit-inline: text edit; `mvn dependency:resolve` as optional verify.
- conflict-missing-version.log: '[ERROR] Could not find artifact ...'. Maven never fails on version conflicts (nearest wins); no machine-readable output.
