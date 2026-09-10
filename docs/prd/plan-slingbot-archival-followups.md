# Plan — Defects found by the SlingBot archival audit

**Status: IN PROGRESS** · PRD: `PRD-SlingBot-Archival-Followups.md`
Issues: #396 · #397 · #398 · #399 · #400
Branch: `fix/slingbot-archival-followups` · One PR, per the one-PR rule.

Test runs are batched: nothing runs the suite until M8. Intermediate milestones are verified by
reading the code and by `dotnet build`.

---

## S1 — Spike: does a real signed delivery verify over the raw-text path? (blocks M2)

**The risk.** Everything measured so far is synthetic. The probe proves the `JObject` path corrupts
and that `GetRawText()` preserves the frame's bytes. It does **not** prove that smee's frame body
equals what GitHub signed. If smee's own Node server re-serializes the payload (`express.json()` →
`JSON.stringify`), canonical GitHub numbers would survive but the guarantee would be luck, not
design — and any non-canonical scalar would break regardless of what we do.

**Measure it, do not reason about it.** Point a real GitHub App's webhook URL at a smee channel,
send one `installation` and one `pull_request` delivery, and capture the raw SSE frame to a file.
Compute `HMAC-SHA256(secret, body_raw_text)` and compare against the frame's `x-hub-signature-256`.

**Cost:** ~45 min. Requires a real App + secret — run locally, never paste the secret into a file,
a prompt, or a commit.

**If it verifies:** proceed with the plan as written.
**If it does not:** the tunnel cannot validate signatures at all, and the design changes to an
explicit, documented, dev-only trust boundary — `AddSmeeDevTunnel` marks the delivery as
tunnel-sourced and the processor skips HMAC for it, gated so it can never be reached in production.
Do not implement that fallback speculatively.

**Record the outcome in this file before writing code.**

---

## M1 — Land the SSE reader in the library

Move the raw-text implementation out of the app and into the package.

- Add `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub.DevTunnel/Services/SmeeSseReader.cs` — a
  `static IAsyncEnumerable<string> ReadFrames(Stream, CancellationToken)` that accumulates `data:`
  lines and yields on a blank line. Split out from the service so it is testable without a socket.
- Rewrite `SmeeBackgroundService` around it:
  - `HttpClient` from `IHttpClientFactory`, `Accept: text/event-stream`,
    `HttpCompletionOption.ResponseHeadersRead`, `Timeout.InfiniteTimeSpan`.
  - Parse each frame with `System.Text.Json.JsonDocument`; skip frames that are not objects or lack
    both `body` and `x-github-event` (smee's ready/ping frames).
  - Body = `LexicalMinify(root.GetProperty("body").GetRawText())`. Keep `LexicalMinify` — it is a
    no-op today and insurance against a future smee that formats its envelope. Make it `internal
    static` on a `SmeeBodyReader` type with `InternalsVisibleTo` for the test project, not `public`.
  - Headers = every top-level string property except `body`, `query`, `timestamp`, into an
    `OrdinalIgnoreCase` dictionary.
  - Thread the `CancellationToken` into `ProcessWebhookAsync`.
- Delete the `async void` handler along with the `SmeeClient` wiring — with our own loop the
  handler becomes an ordinary `await`, which removes the whole class of unobserved-exception risk
  that the current `try/catch` only papers over.

**Fixes D1.** Verify: build clean, and the `Deserialize`/`Serialize` pair is gone from the file.

## M2 — Reconnect and clean shutdown

- Wrap the read loop in `while (!stoppingToken.IsCancellationRequested)` with a 5 s delay on
  failure, distinguishing `OperationCanceledException` during shutdown (return) from a real fault
  (log warning, retry).
- Put the `Task.Delay(..., stoppingToken)` **inside** the `try`, so a graceful stop does not throw
  `TaskCanceledException` out of `ExecuteAsync` — the bug currently present at
  `SmeeWebhookTunnelService.cs:64`.
- Keep the existing early return when `ChannelUrl` is empty.

**Fixes D4 and D5.**

## M3 — Drop `Smee.IO.Client`

- Remove the `PackageReference` from
  `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub.DevTunnel/…csproj`.
- Remove `Newtonsoft.Json` from that csproj too **if** nothing else in the project uses it — check
  first; the WebSocket dev client may rely on it via `SocketExtensions`.
- Delete `MintPlayer.Spark.Webhooks.GitHub.DevTunnel.csproj.Backup.tmp` and add `*.csproj.Backup.tmp`
  to `.gitignore`.

**Fixes D8** and acceptance criterion 4.

## M4 — Consolidate: CodeCoverage uses the library tunnel

- In `apps/CodeCoverage/CodeCoverage/Program.cs`: replace the "Deliberately NOT
  `options.AddSmeeDevTunnel`" comment block (~:181-187) with a real
  `options.AddSmeeDevTunnel(builder.Configuration["GitHub:SmeeChannelUrl"])`, guarded on
  non-empty as today; delete the `AddHostedService<SmeeWebhookTunnelService>()` registration (~:199-201).
- Delete `apps/CodeCoverage/CodeCoverage/Services/SmeeWebhookTunnelService.cs`.
- Move `apps/CodeCoverage/CodeCoverage.Tests/Services/SmeeWebhookTunnelServiceTests.cs` to
  `tests/MintPlayer.Spark.Tests/Webhooks/DevTunnel/SmeeBodyReaderTests.cs`, retargeting it at the
  library type. Do not drop coverage in the move — port every existing case.

**Fixes D6.** This touches the production app; it is a dev-only code path, but say so in the PR.

## M5 — Per-client webhook routing

Restore the capability SlingBot had, without reintroducing its subclass-based extension model.

- Add to the options a routing delegate, defaulting to sender-match:
  `Func<GitHubWebhookRoutingContext, SocketClient, bool> DevSocketFilter`.
  The context carries the parsed-but-untrusted routing fields already extracted in
  `SparkWebhookEventProcessor` (sender login, repository full name, installation id) — reuse that
  extraction rather than re-parsing, and keep it off the signed-body path.
- `IDevWebSocketService.SendToClients` takes the context and applies the filter; the default matches
  `SocketClient.GitHubUsername` against the sender login, case-insensitively.
- **Decide the default deliberately** and record it here: a sender-match default changes today's
  behaviour, so provide `DevSocketFilter = static (_, _) => true` as the documented opt-out for
  anyone who wants the old broadcast.
- Document it in the DevTunnel `README.md`.

**Fixes D2** and gives `GitHubUsername` its first read (criterion 8).

## M6 — Package version and licence

- `libs/socket_extensions/MintPlayer.Dotnet.SocketExtensions/…csproj`: `<Version>10.0.1</Version>`
  (from `10.0.0-preview.80`). Licence stays MIT — no change, but confirm it in review.
- Verify CI packs it as stable and does **not** pull the other 23 packages off
  `10.0.0-preview.80`: inspect `.github/workflows/dotnet-build-master.yml` and confirm the version
  comes from each csproj, not a shared property.
- Note in the PR description that SlingBot's `10.0.0` should be deprecated on nuget.org pointing at
  `10.0.1`. That is a registry action outside this repo — do not attempt it from CI.

**Fixes D3.** ⚠️ Per `CLAUDE.md`, a wrong version on `master` is published permanently. Check the
version diff explicitly in review.

## M7 — Correct the record

- Rewrite the comments that misdiagnose the cause: the surviving one in the library, and any trace
  left in CodeCoverage. State that the corruption occurs inside the client library's `JObject`
  materialization, that a lexical minifier over the parsed object does not fix it, and that the
  design rule is *capture the raw bytes, never reconstruct them*.
- Remove the reference to `docs/spark-handoff.md`, which does not exist. Point at
  `docs/prd/PRD-SlingBot-Archival-Followups.md` §1 instead.
- Add a short section to `docs/guide-*` webhooks documentation (or the DevTunnel `README.md`)
  covering the dev tunnel and the routing filter.

**Fixes D7.**

## M8 — Verification sweep

The single test run for the whole task.

1. `dotnet build MintPlayer.Spark.sln` → clean.
2. Full suite, output captured to a file:
   `dotnet test … > <scratchpad>/tests.log 2>&1; echo "EXIT: $?"` — then filter the log. Never pipe
   the only copy into `grep`/`tail`.
3. New tests to have written along the way (all run here for the first time):
   - `SmeeBodyReaderTests` — a frame containing `"…:12.000+02:00"`, `1.50`, an integer beyond
     `long.MaxValue`, and `a\/b`; assert the extracted body is byte-identical to the frame's `body`
     span. **Run it under two timezones** (`TZ=UTC` and `TZ=Europe/Brussels`) to pin criterion 3 —
     this is the assertion that would have caught the original bug.
   - `SmeeSseReaderTests` — multi-line `data:` frames, blank-line termination, ready/ping frames
     ignored.
   - A signature test: HMAC over the extracted body matches a signature computed over the original
     bytes.
   - `DevWebSocketService` routing — two clients, different usernames, one webhook, one recipient;
     plus the broadcast opt-out.
   - Shutdown test: cancel the token, assert `ExecuteAsync` completes without throwing.
4. Confirm `grep -ri "smee.io.client" --include=*.csproj` returns nothing.
5. Re-read the version diff on `MintPlayer.Dotnet.SocketExtensions`.

---

## Notes

- **Do not run the Angular dev server** for any of this; nothing here touches a SPA.
- The dev tunnel cannot be exercised end-to-end in CI (it needs a real smee channel). S1 is the only
  live-fire check and it is manual — say so plainly in the PR rather than implying CI covers it.
- `apps/CodeCoverage` is production. The changed path is dev-only, but the app still must build and
  its tests still must pass.
