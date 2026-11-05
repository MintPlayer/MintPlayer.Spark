# Research 3: in-repo merge queue / git-writer vs issue #568 (backport)

Paths relative to C:\Repos\MintPlayer.Spark\apps\CodeCoverage unless noted.

## HEADLINE
A PR backport feature ALREADY EXISTS (#153 M10, versioning PRD D10, shipped as part of package versioning, "not yet deployed" per docs/code-coverage/README.md:19). #568 is therefore an extension (conflict resolution, config location, scope), not greenfield.
Docs: docs/code-coverage/versioning.md:219-238; docs/coverage_package_versioning_PRD.md (D10, D12, D17, line 41, 56, 125).

Existing flow:
1. Trigger (webhook normaliser) CodeCoverage.GithubIntegration/Recipients/GitHubEventsRecipient.cs:506-552 (pull_request closed+merged with label -> BackportRequested, Source=Merge, no role check since merge needed write), :513-518 (labeled on already-merged PR), :576-585 (issue_comment `/backport 8.x 9.x`). Syntax parser: CodeCoverage.Library/Forge/BackportCommand.cs (label `backport: 8.x` / `backport 8.x`; `/backport` first line; major, `N.x` or branch name; max 10 lines, 100 chars). Event: CodeCoverage.Library/Forge/ForgeEvents.cs:344 `BackportRequested(RepositoryId, Number, Lines, ActorLogin, EBackportSource{Merge,Label,Comment})`.
2. Queueing: CodeCoverage/PackageVersioning/BackportRecipients.cs:25-37 BackportEventsRecipient -> `BackportMessage` on queue CoverageQueues.Versioning (MessageQueue).
3. Orchestration: BackportRecipients.cs:50-246 `BackportRecipient`:
   - role check Write for Label/Comment (:91-93, via IForgeMergeQueue.GetActorRoleAsync);
   - needs merged PR + MergeCommitSha (:96);
   - reads coverage.yml from default branch (:101) via `VersioningYml.Parse` (CodeCoverage/PackageVersioning/VersioningYml.cs); lines resolved against `versioning.releaseBranches` (`config.EffectiveReleaseBranches`, default pattern `release/*.x` per VersioningOnboarding.cs:59) in `Resolve` (:220) using ReleaseBranchCuts.NameFor / ReleaseBranches.IsMatch;
   - target head branch `backport/<pr>-<8.x|branch-with-dashes>` (:116, :229);
   - gets writer credentials `git.GetWriterAccessAsync` (:132) = per-repo installation token, revoked in finally (:157);
   - calls `writer.VersioningBackportAsync` (IGitWriterSidecar; client CodeCoverage/MergeQueue/GitWriterSidecarClient.cs);
   - on all-applied: opens a PR per line via `forge.OpenPullRequestAsync` titled `[8.x] <title>` (:177), comments summary on original PR; on conflict: comment with files + manual `git cherry-pick -x` commands (ConflictComment :197-216); failures comment text; missing writer -> "add the label again to retry".
   - All forge calls via IForgeVersioning (CodeCoverage.Library/Forge/IForgeVersioning.cs: CommentAsync, GetPullRequestTextAsync -> ForgePullRequestText{Merged, MergeCommitSha, BaseRef, Title, Number}, OpenPullRequestAsync) + IForgeMergeQueue (GetBranchHeadAsync, GetPullRequestCommitsAsync, GetActorRoleAsync).

## 1. Git-writer
- Project: CodeCoverage.GitWriter (own Dockerfile, Program, GitWriterHost.cs:40 registers BackportService singleton; options GitWriterOptions.cs). Tests: CodeCoverage.GitWriter.Tests (BackportTests.cs, WriterHost.cs, GitHttpServer fake). Docker-compose service (docker-compose.yml, ~25 refs), deploy via .github/workflows/code-coverage-deploy.yml.
- Pure plumbing in a bare repo (no worktree, no hooks): JobRunner.RunAsync(kind, repo, ctx) (Jobs/JobRunner.cs) gives per-job temp bare repo, disk cap (`EnsureWithinDisk`), timeouts, ElapsedMilliseconds; outcomes in Jobs/JobContracts.cs:~416 JobOutcomes (Backported, Conflicted, ...), JobReasons (Unplannable...).
- Endpoints (Endpoints/*.cs, IPostEndpoint<T> classes, `Path`): GET /healthz; POST /jobs/prepare, /jobs/queue-branch, /jobs/publish (GitWriterEndpoints.cs:18-183; merge queue); /jobs/tags (:183); /jobs/dependencies/{work-ref,validate,rolling,files} (DependencyEndpoints.cs); /jobs/versioning/{run,plan,onboarding,backport} (VersioningEndpoints.cs:26,107,145,200).
- Credentials: request carries `cloneUrl` + `credential` (CredentialWire, username/password). The app mints it per request: GitHubForgeMergeQueue.cs:1146-1197 installation token restricted to `repository_ids` = this repo, `permissions: {contents: write}` plus `workflows: write` only when needed. WireValidation.RemoteUrl/Credential validate (Jobs/WireValidation.cs).
- Backport job (Jobs/BackportService.cs, 185 lines), the closest existing thing to "apply PR X onto branch Y":
  - init bare repo, fetch source branch + targets (`+refs/heads/b:refs/heads/b`, :40-43), verify commit is ancestor of source (:46);
  - ChangeAsync (:115-137): determines mode merge (parent0..c), rebase (subjects match last N first-parent commits -> base = run[0].parent), squash (c vs parent);
  - per target `git merge-tree --write-tree -z --messages --merge-base=<base> <tip> <commit>` (:57) = in-memory 3-way cherry-pick; conflicts from MergeTreeOutput.Stages + CONFLICT messages (:69-71);
  - ALL-OR-NOTHING: any conflict -> outcome Conflicted, nothing pushed (:76-78). Comment at :19: "no conflict resolver runs on old lines";
  - builds commit with `commit-tree <tree> -p <tip> -F -`, original author+date, committer = VersioningController.Tagger; message = title (#pr) or squash message + `(cherry picked from commit sha)` trailers (:147-158);
  - pushes to `backport/...` via WriterService.PushAsync with expected: null (create-only lease): Created/Exists/Rejected; Empty when tree equals tip tree (already holds change).
  - Wire (JobContracts.cs:330-368): Provider, RepoId, CloneUrl, Credential, SourceBranch, Commit, PullRequest, Title, Subjects, Committer, Targets[{Branch, Head}]. Endpoint validates: Head must start `backport/`, <=10 targets, branch != source, distinct (VersioningEndpoints.cs:189-247, `Targets`).
- To add conflict resolution to this job, the BackportService would need: ConflictResolverRegistry (DI singleton, already in the writer assembly sharing), AutoResolveRule list on the wire, then run MergeQueueResolver (Git/MergeQueue/MergeQueueResolver.cs) instead of raw merge-tree. Also a `workflows` token permission if the change touches .github/workflows (GitHubForgeMergeQueue.cs:1191-1197; PatchValidator.IsWritablePath/IsUnderGitHub), and `regenerate` workflow dispatch (not in the backport path today).
- Other merge-queue-ish building blocks: Jobs/WriterService.cs (PushAsync, Oid, merge replay), CodeCoverage.Git/Dependencies/RollingBranchReplay.cs ("worktree-less cherry-pick", PRD line 41), PatchCombiner/PatchFile/PatchValidator.

## 2. Conflict resolution pipeline (CodeCoverage.Git/MergeQueue)
- Interface: Resolution/IConflictResolver.cs:14: `Strategy`, `RegeneratesByDefault`, `MaxSideBytes`, `CanResolve(path, stages, rule)`, `ResolveAsync(ConflictInput)` -> `ConflictResolution` = UseStage(n) | Content(text) | Refused(EvictionReason, detail). ConflictInput(Path, Rule, Stages 1=base/2=ours/3=theirs, MergedTree, ConflictRepository). Pure function of blobs; deterministic (D65).
- Built-ins (Resolution/ConflictResolverRegistry.cs:31): TakeBaseResolver (stage 2; TakeBaseResolver.cs), KeepBothResolver (JSON keep-both of additive keys), LineSetResolver (line-set unions e.g. .gitignore), EvictResolver (TakeBaseResolver.cs:72 same file). Validators (:36): Utf8, Json, JsonNoDuplicateKeys, Xml, NoConflictMarkers, Yaml, GitHubWorkflow. Registry builds from IEnumerable<> ([Register] has no keyed overload); names must equal MergeQueueWireRules.Strategies/Validators (test-enforced, D57). Other pieces: Diff3Document.cs, YamlDocument.cs, ConflictHunks.cs, AutoResolvePresets.cs (e.g. SparkModelHashes), AutoResolveRule.cs (`FromWire`, `Create`, glob path, keys, anyLine, validate, regenerate).
- Engine: MergeQueueResolver.cs (`ResolverVersion = "resolution-2"`): git merge-tree without checkout/hooks/attributes; first matching autoResolve rule per conflicted path; one refusal evicts the PR; MergeOutcome = Clean | Resolved(BaseCommit, HeadCommit, Tree, Paths, Resolutions[ResolvedPath(Path, Strategy, Regenerate, Rule)]) | Evicted(reason). Non-content conflicts (rename/delete) are evicted. Output never contains markers.
- Generic vs queue-specific: resolvers/validators/rules/MergeOutcome are generic over (base tree, ours, theirs, merge-base) and could serve a cherry-pick: merge-tree with `--merge-base=<pr parent>` ours=<release tip> theirs=<pr commit> yields exactly the same stage entries. Tied to "merge default into PR branch": the Prepare/QueueBranch/Publish jobs (WriterService.cs), "take-base" semantics (stage 2 = base branch; for backport stage 2 = release branch, still sensible), the saga in CodeCoverage/MergeQueue (engine, check runs, eviction messages), and `regenerate` = workflow dispatch on the queue branch (app maps rule index -> workflow, O36).
- Regeneration (lockfiles/markers): not in the writer; app dispatches a workflow (`regeneratedPaths`, per-rule `regenerate`/`action`) against the queue branch, then writer republishes. Spec: docs/coverage_merge_queue_PRD.md §0, §10, D93-D105, O33-O37. For a backport PR branch the equivalent would be dispatching the same workflow on `backport/...` before opening the PR (NEW work).

## 3. GitHub App surface
- Webhooks normalised in CodeCoverage.GithubIntegration/Recipients/GitHubEventsRecipient.cs into forge events (docs/code-coverage/forge-webhooks.md:71,97 table; GitLab/Bitbucket columns exist). Handled: pull_request closed/merged, labeled (backport + merge-queue label), issue_comment created (`/queue`-style merge-queue commands, dependency commands, `/backport`); actor = `sender`, bots/App ignored.
- Recipients are `IRecipient<ForgeWebhookMessage<TEvent>>` -> broadcast to MintPlayer.Spark.Messaging queues (CoverageQueues.cs; Versioning queue for backports).
- Merge-queue UI on the PR: check run with a button (CodeCoverage.Tests/MergeQueue/MergeQueueCheckButtonTests.cs, MergeQueueCheckRunTests.cs) + comments; GitHubForgeMergeQueue.cs (GitHub impl of IForgeMergeQueue incl. token minting :1146-1197), IForgeMergeQueue at CodeCoverage.Library/Forge/IForgeMergeQueue.cs.
- Permissions already requested: contents write (token), pull requests, checks, Actions write + Workflows write (docs/code-coverage/dependency-updates-PRD.md:308; hosting.md:450 about accepting new permissions). So backport opening PRs / pushing backport/* branches needs no new App permission.
- coverage.yml: file name via CoverageYml.FileName; `versioning:` section parsed by CodeCoverage/PackageVersioning/VersioningYml.cs (strict: invalid section refuses run, no fallback; VersioningController); `mergeQueue`/`autoResolve` section parsed for the merge queue (Git/MergeQueue AutoResolveRule.FromWire; MergeQueueWireRules). Release-branch patterns: `versioning.releaseBranches` (`EffectiveReleaseBranches`; mirrored into VersioningDispatchState.cs:20). A new `backport:` section would be parsed beside these (new parser or extend VersioningYml), would reuse the same `autoResolve` rule shape, and would be sent to the writer as wire (add `AutoResolve` list to VersioningBackportRequestWire, validated by AutoResolveRule.FromWire). Note: today backport depends on a `versioning` section being present (BackportRecipients.cs:101-103 refuses otherwise) - decouple if #568 wants backports without versioning.
- Provider abstraction: IForgeVersioning / IForgeMergeQueue per provider (GitHub, GitLab, Bitbucket forge libs); a backport change that adds forge calls must touch these interfaces.

## 4. Existing release-branch / backport / cherry-pick content
- Auto-cut release branches: CodeCoverage/PackageVersioning/ReleaseBranchCutRecipient.cs (D12; cuts `release/N.x` at merge's first parent when platform major moves; `Repository.ResolveAutoCutReleaseBranches`, migration M_202610241300_BackfillVersioningDefaults.cs).
- DispatchRecipients.cs:41,84 treat release branches as versioned lines (state.ReleaseBranches).
- versioning-action/src/releases.ts (+test) mentions backport/release lines.
- Tests: CodeCoverage.Tests/PackageVersioning/BackportTests.cs, BackportRecipientTests.cs, ReleaseBranchCutRecipientTests.cs; GitWriter.Tests/BackportTests.cs; GitGuardian etc. n/a.
- Hosting advice: protect `release/*` with ruleset (versioning.md:236-238).

## Gaps for #568 relative to what exists
- No conflict resolution in backport (explicitly excluded, BackportService.cs:19); all-or-nothing across lines.
- No regenerate dispatch / validators on backport.
- Config is tied to `versioning.releaseBranches`; no `backport:` section.
- No automatic backport by label-on-merge config (rules like "label X -> branch Y")? Label-on-merge exists (backport: <line>).
- Backport PR body/labels minimal; no merge queue integration for the backport PR.
- Not yet deployed per README; verify current deploy status before relying on it.
