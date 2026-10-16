# Source: S13 apply spike, nuget

Generated 2026-10-15 by `spikes/s13/run-nuget.sh, run-nuget-conflict.sh` (sample repo built under `spikes/s13/work/`, one base commit, one pinned bump per case).
Each `*.diff` is `git diff` (untracked files included via `git add -N`) after the case's command, against the base commit.
Timings: `spikes/s13/logs/_timings.txt`; raw tool output: `spikes/s13/logs/<case>.log`.

dotnet SDK 11.0.100-rc.1, RestorePackagesWithLockFile=true.
- csproj-native-add: `dotnet add package Newtonsoft.Json --version 13.0.3` - collapses whitespace in the attribute and rewrites an unrelated line (`"all"/>` -> `"all" />`) (NOT pure).
- csproj-native-add-child-element: same, <Version> child element - updates it but again touches unrelated lines.
- csproj-textedit-restore: text edit + `dotnet restore --force-evaluate` - pure + packages.lock.json. RECOMMENDED.
- cpm-native-add: `dotnet add package` under CPM rewrites the WHOLE Directory.Packages.props (line endings, final newline, moves the trailing comment to its own line).
- cpm-textedit-restore: text edit of <PackageVersion> + restore - pure. RECOMMENDED.
- conflict-NU1605.project.assets.json: restore exit 1; obj/project.assets.json `logs[]` = {code:NU1605, level:Error, libraryId:Serilog, targetGraphs, message} - machine-readable. Restore also rewrote packages.lock.json on failure -> adapter must reset the group.
- tools-native-update: `dotnet tool update --local --version` rewrites dotnet-tools.json (reflows arrays, adds rollForward, drops final newline).
- tools-textedit: text edit + `dotnet tool restore` (verify) - pure. RECOMMENDED.
- global.json: no native command; text edit only (no diff captured, trivially pure).
