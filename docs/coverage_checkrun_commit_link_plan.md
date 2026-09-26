# Plan — A check run that links to the commit it measured

Companion to [`coverage_checkrun_commit_link_PRD.md`](coverage_checkrun_commit_link_PRD.md).
**Status:** not started. SP1 and SP2 gate M1.

One pull request, per the repository rule — the link, the shared URL builder, the authorization
correction from F8 and the fork-id resolution from F9 all land together.

---

## Spikes — run before M1

SP1 and SP2 are blocking: each can invalidate the design. SP3–SP5 refine it and can run alongside M1.

### SP1 (BLOCKING) — the anonymous 401

Measured on production 2026-09-25: `/api/browse/*` and `/spark/po/load` answer `401` with
`WWW-Authenticate: Bearer`, while `securityPosture.txt` lists both surfaces as anonymous and the
code that grants it shipped 2026-09-01 (`e748eee5`).

```
dotnet run --launch-profile https      # bare `dotnet run` breaks GitHub sign-in
curl -s -o /dev/null -w "%{http_code}\n" \
  http://localhost:5200/api/browse/repos/github/MintPlayer/MintPlayer.Spark
```

- **200 locally** → the divergence is environmental. Compare `docker-compose.yml` env against
  `appsettings.json`, and check the Traefik rules at `docker-compose.yml:205`.
- **401 locally** → confirm the default challenge scheme. Inspect the authentication builder around
  `Program.cs:300-330` (the GitHub OIDC upload scheme, `ValidAudience` at `:318`) and check whether
  it registers as default rather than named. Verify by temporarily naming the scheme on the upload
  endpoints only and re-probing browse.

Record the answer in this file before writing code. **Do not** loosen `security.json` to make the
probe pass — the grants there are already correct (F8); the defect is upstream of them.

### SP2 (BLOCKING) — the fork/PR commit id

`UploadIngestor.cs:88` passes a `prSegment` into `Commit.DocumentId`, while `Commit.cs:165-169` says
"Nothing writes it today" and every `BrowseController` lookup (`:291`, `:297`, `:362`, `:442`, `:501`)
uses the non-PR overload.

1. Read `UploadIngestor.cs` around `:60-100` and establish what `prSegment` is for a first-party PR
   build versus a fork upload (`ForkUploadsController.cs:51`).
2. Confirm against real data whether any `Commits/github/{repoId}/pr/{n}/{sha}` documents exist —
   a RavenDB id-prefix query, read-only.

- **No PR-scoped documents** → the doc-comment is right, `GetCommit` is fine, M2 is unaffected.
- **PR-scoped documents exist** → the vanity route (`app.routes.ts:60`) structurally cannot address
  them, and M2 grows: either `GetCommit` tries both shapes, or the route gains an optional PR
  segment. Prefer the former — it keeps the published URL shape stable, which matters because these
  URLs land in check runs that are never rewritten.

### SP3 — Octokit `DetailsUrl` update semantics

Against a scratch repository with the development App: create a check run with a `details_url`, then
update it with `DetailsUrl = null`, and read both back. Record whether null clears, preserves, or
reverts to the App-homepage fallback. Settles D3 and D4.

### SP4 — what a logged-out visitor sees

`curl` cannot answer this: the SPA returns 200 with an identical 5419-byte body for every path,
including fabricated ones. Use the `playwright_node` MCP server directly (**not** the `dcg:playwright`
skill). Components are in shadow DOM — use a recursive `deepFind` over `shadowRoot`s inside
`browser_evaluate`, and return only primitives or the result can blow the token limit.

Navigate logged-out to `{baseUrl}/github/r/{owner}/{repo}/c/{sha}` and record: report rendered,
sign-in shown, or bounced to `HOME_URL`. Repeat for the repository-level URL to document the current
state of the already-published `[full report]` link.

### SP5 — private-repository click-through

Same harness, a private repository, a viewer without access. Record whether the outcome distinguishes
"not signed in" from "not permitted", and confirm it does not become an existence oracle (F8 measured
401-not-404 on a nonexistent repository — that property must survive).

---

## Milestones

Commit per milestone. **Test suites run once, at M8** — intermediate milestones are verified by
reading the code and building.

### M1 — Fix the anonymous read surface (blocked on SP1)

Apply SP1's root cause so `/api/browse/*` and the Spark read endpoints are reachable without
credentials, restoring the posture `securityPosture.txt` already documents. Row-level scoping in
`RepositoryVisibility.cs:35-36` is untouched: public repo visible, private repo refused.

Regenerate `securityPosture.txt` and review the diff in the PR — it is a CI-gated artifact.

### M2 — Resolve a commit by sha, whatever id shape it was stored under (blocked on SP2)

Only if SP2 finds PR-scoped documents. Make `BrowseController.GetCommit` (and the `/tree`,
`/hierarchy`, `/file` siblings at `:362`, `:442`, `:501`) find a commit under either id shape. Keep
the build-prefix streaming at `:296-311` consistent with whichever id resolved — it currently
recomputes the prefix from the non-PR shape rather than reusing `commit.Id`, which would silently
return zero builds for a PR-scoped commit.

### M3 — The shared URL builder

New static helper in `CodeCoverage.Library` beside the entities:

```csharp
public static string? Repository(string? baseUrl, Repository repo);
public static string? Commit(string? baseUrl, Repository repo, string sha);
```

Null/empty `baseUrl` → null. `TrimEnd('/')`. Provider via `EForgeProvider.ToCanonicalString()`.
Unit tests: both shapes, the null-base-url case, and a non-GitHub provider spelling.

### M4 — `ForgeVerdict.DetailsUrl`

Add the optional trailing member (D1) with a doc-comment recording the per-forge mapping — GitHub
`details_url`, GitLab `target_url`, Bitbucket `url` (**required**). Update the test double tuple at
`ScriptedDiffService.cs:69` and the conformance fakes at `ForgeIntegrationConformanceTests.cs:43`,
`:183`, `:207`.

### M5 — Compose the URL and the visible link

In `PublishFeedbackRecipient.ToVerdict` (`:175-187`), beside the existing fork post-processing:
set `DetailsUrl` from `UrlBuilder.Commit(...)` and append the `[View the full report for {shortSha}]`
line to the summary. Omit the visible line and log a warning when `Coverage:BaseUrl` is unset (D4).

`GateEvaluator` is deliberately untouched (F11) — no `GateEvaluatorTests` expectation should move.

### M6 — Set it on the wire, both paths

`GitHubForgeFeedbackPublisher.PostCheckRunAsync` (`:57-92`): `DetailsUrl = verdict.DetailsUrl` on
`NewCheckRun` (`:85-90`) **and** `CheckRunUpdate` (`:76-82`), per SP3's answer.

### M7 — The PR comment footer goes one segment deeper

`PullRequestCommentRenderer.AppendFooter` (`:159-161`) switches to `UrlBuilder.Commit(...)`; refactor
`BadgeMarkdown` (`:125`, `:132`) onto `UrlBuilder.Repository(...)`. The badge's own link stays
repository-level. Preserve the `⚠` comment at `:151-156` about the forge-side commit URL — that is
still true and still deferred.

### M8 — Tests, docs, and the full sweep

- A payload test for `PostCheckRunAsync` — the first one it has ever had (F10) — asserting
  `details_url` on create **and** update.
- Extend `ForkFeedbackPublishTests.cs` (`:137-148`) to assert the details URL reaches the publisher
  for a fork build.
- Extend `PullRequestCommentRendererTests.cs` for the commit-level footer; the existing
  `No_base_url_configured_omits_links_rather_than_emitting_relative_ones` (`:219`) must still pass.
- Update `docs/code-coverage/` for the new link and, if M1 changed anything, the anonymous surface.
- Run **all five** test projects against the `.slnx` solution, output redirected to a file, once.

---

## Sequencing notes

- M1 and M2 are correctness fixes to the destination; M3–M7 point at it. Landing M5–M7 first would
  publish confident links to a bounce, which is worse than the status quo (F1: the current link is
  useless but harmless).
- M3 precedes M4–M7: all three call sites consume the builder.
- M6 depends on SP3 only for the null-on-update question; the non-null path is unconditional.

---

## Risks

- **SP1 finds an environmental cause.** Then M1 is a deployment change, not a code change, and the
  PR cannot fully verify it. Mitigation: verify against production after deploy and record the
  measurement; do not mark criterion 3 met on a local-only result.
- **SP2 finds PR-scoped documents.** M2 grows and the route may need revisiting. The published URL
  shape must stay stable — check runs are never rewritten, so a URL shape shipped here is permanent
  in every historical PR.
- **Regenerating `securityPosture.txt`** is CI-gated; an unreviewed diff there is exactly the kind of
  change that should not slip through. Read it line by line in review.
- **`GitGuardian` will flag nothing here**, but the model/description sync gate and the `libs/`
  version-bump gate are CI-only — neither should trigger, since this PR touches `apps/` only. If a
  `libs/` file does get touched, the version bump is mandatory.
- **Verification by HTTP status is invalid** for anything user-facing in this app (F8). Every
  acceptance claim about a page must come from rendered DOM.

---

## Out of scope

Carried from the PRD: the M15 forge-side commit-URL builder; GitLab/Bitbucket publishers;
`output.text`, annotations and `external_id`; a patch/diff-only page; binding a `CoverageOptions`
class.
