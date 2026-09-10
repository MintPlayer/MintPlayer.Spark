# PRD — Defects found by the SlingBot archival audit

**Status: IN PROGRESS** · Plan: `plan-slingbot-archival-followups.md`
Issues: #396 (D1, D4) · #397 (D5, D6) · #398 (D2) · #399 (D3) · #400 (D7, D8) — all land in **one** PR.

The `MintPlayer/MintPlayer.SlingBot` repository was archived and transferred to
`MintPlayer-Archive/MintPlayer.SlingBot` on 2026-09-10. Before archiving, a four-way audit compared
every capability in that repo against this one. The absorption is complete — every webhook
capability is present in `libs/webhooks/**` and several are materially hardened — but the audit
surfaced eight defects in **this** repository. This PRD covers those defects.

`MintPlayer.DiffParser{,.Abstractions,.Data}` has deliberately **not** been ported. CheckRun
annotations are more lenient about positioning than pull-request review comments, so the line-level
diff model the parser existed to feed is no longer needed. Those three package IDs are frozen at
`10.0.0` on nuget.org with no upstream, which is accepted.

---

## 1. The headline defect: dev webhooks are silently dropped

`SmeeBackgroundService` (`libs/webhooks/MintPlayer.Spark.Webhooks.GitHub.DevTunnel/Services/SmeeBackgroundService.cs:45-51`)
reconstructs the webhook body before handing it to the processor:

```csharp
// Re-minimize JSON to match the exact bytes GitHub signed
// (smee.io may reformat/pretty-print the JSON during SSE relay)
var minifiedBody = JsonConvert.SerializeObject(
    JsonConvert.DeserializeObject(e.Data.Body.ToString() ?? throw ...));
```

Since `SparkWebhookEventProcessor` now validates the HMAC **unconditionally and fail-closed**, a body
that does not reproduce GitHub's exact bytes fails verification and the delivery is discarded. For
any event type carrying a timestamp — which includes `installation` — that is every delivery.

### 1.1 The stated cause is wrong, and the difference matters

Both the code comment above and the longer one in
`apps/CodeCoverage/CodeCoverage/Services/SmeeWebhookTunnelService.cs:7-21` attribute the problem to
smee.io relaying the payload pretty-printed, with the Newtonsoft round-trip as the thing that
reinterprets scalars. The second half is true in isolation; the first half is not, and the
conclusion drawn from it — that a lexical minifier is the fix — does not hold **inside the library**.

Measured against `Smee.IO.Client` 1.0.16 (probe run 2026-09-10, host TZ `+01:00`):

| Stage | Value |
|---|---|
| GitHub's signed bytes | `{"created_at":"2024-01-01T12:00:12.000+02:00","amount":1.50,"esc":"a\/b"}` |
| `e.Data.Body.ToString()` | `{\n  "created_at": "2024-01-01T11:00:12+01:00",\n  "amount": 1.5,\n  "esc": "a/b"\n}` |
| Current output (round-trip) | `{"created_at":"2024-01-01T11:00:12+01:00","amount":1.5,"esc":"a/b"}` |
| `LexicalMinify(Body.ToString())` | `{"created_at":"2024-01-01T11:00:12+01:00","amount":1.5,"esc":"a/b"}` |

The round-trip and the lexical minifier produce **byte-identical, equally wrong** output. The
corruption does not happen in our code at all: `Smee.IO.Client`'s `SmeeData.Body` is typed
`System.Object` and materializes as a Newtonsoft `JObject`
(`Smee.IO.Client.Dto.SmeeEvent` → `SmeeData.Body`, confirmed by reflection), so the raw bytes are
already gone at the moment `OnMessage` fires. Nothing downstream can recover them.

Two further observations from the same probe:

- **The corruption is machine-dependent.** `+02:00` became `+01:00` — Newtonsoft's default
  `DateParseHandling` does not merely strip fractional seconds, it rebases the instant into the
  host's local timezone. Two developers in different zones produce different wrong bytes.
- **Escape normalization also occurs** (`a\/b` → `a/b`). Whether GitHub ever emits `\/` is
  unverified, so treat this as latent rather than a confirmed second failure mode.

Also note smee.io does **not** pretty-print: it emits the envelope as a single minified SSE `data:`
line. There is no whitespace to strip in the first place.

### 1.2 CodeCoverage's implementation is correct; only its explanation is wrong

`SmeeWebhookTunnelService` applies `LexicalMinify` to `body.GetRawText()` taken from the **raw SSE
frame**, not to a parsed object. `JsonDocument.GetRawText()` returns the original text span, so
`1.50`, `.000+02:00` and `a\/b` all survive intact — verified in the same probe. The implementation
works. Its comment misdiagnoses why, and that misdiagnosis is what would mislead the next person
into "just port `LexicalMinify` into the library."

Because the frame is already minified, `LexicalMinify` is in practice a no-op. It is retained as
cheap insurance against a future smee that formats its envelope.

### 1.3 Designs considered

| Option | Verdict |
|---|---|
| **A.** Newtonsoft `Deserialize`→`Serialize` (current) | **Rejected** — measured corruption. |
| **B.** Port `LexicalMinify` over `Body.ToString()` | **Rejected** — measured byte-identical to A. The obvious fix, and it does not work. |
| **C.** Configure `DateParseHandling.None` / `FloatParseHandling` | **Rejected** — the deserialization happens inside `Smee.IO.Client`, reachable only by mutating the global `JsonConvert.DefaultSettings`, which would silently change JSON behaviour for the entire host application. It also leaves escape normalization unaddressed. |
| **D.** Own SSE reader; extract the body as raw text and never parse it into an object model | **Chosen.** |

**The principle: never reconstruct the signed bytes — capture them.** Any design that routes the
body through a JSON object model has already lost, regardless of how carefully it re-serializes.

---

## 2. Defect register

| ID | Defect | Severity |
|---|---|---|
| **D1** | smee dev tunnel corrupts the signed body; deliveries silently dropped (#396) | High — dev tunnel unusable |
| **D2** | Dev WebSocket tunnel broadcasts every webhook to every connected developer (#398) | Medium — regression vs SlingBot |
| **D3** | `MintPlayer.Dotnet.SocketExtensions` 10.0.0 collision: consumers get SlingBot's uncapped build (#399) | Medium — affects published artifacts |
| **D4** | Library smee tunnel has no reconnect; exits permanently on first failure (#396) | Medium |
| **D5** | `SmeeWebhookTunnelService` throws `TaskCanceledException` out of `ExecuteAsync` on shutdown (#397) | Low |
| **D6** | Two smee implementations; the correct one is app-local and unreachable by other consumers (#397) | Medium — maintenance |
| **D7** | Misleading comments + dangling reference to a non-existent `docs/spark-handoff.md` (#400) | Low |
| **D8** | Stray `MintPlayer.Spark.Webhooks.GitHub.DevTunnel.csproj.Backup.tmp` checked into the tree (#400) | Low |

### D2 — per-client routing was not ported

SlingBot let the consumer decide which connected developer received a given webhook, via an abstract
`GetDevSocketsForWebhook` hook; its demo matched `SocketClient.GithubUsername == webhook.Sender?.Login`.

Spark kept the field but never reads it — `SocketClient.GitHubUsername`
(`libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/Services/SocketClient.cs:8`) has exactly one write
and no reads repo-wide. `DevWebSocketService.SendToClients`
(`.../Services/DevWebSocketService.cs:33-43`) fans out to every open socket.

`AllowedDevUsers` is not a substitute: it gates **connection**, not **routing**. It fails closed when
empty, so an unlisted developer receives nothing — but any two listed developers now see each
other's webhook traffic, including payloads for repositories the other may not work on.

### D3 — the version collision

nuget.org's version list for `MintPlayer.Dotnet.SocketExtensions` ends `10.0.0-preview.80, 10.0.0`.
The stable `10.0.0` was published from SlingBot (its nuspec carries
`RepositoryUrl = .../MintPlayer.SlingBot`, Apache-2.0). Spark only ever ships previews, which sort
below any stable, so `dotnet add package MintPlayer.Dotnet.SocketExtensions` resolves to **SlingBot's
build — the one without the 1 MiB message cap**. Spark can never publish `10.0.0`; that number is
permanently burned.

Two decisions taken:

- **Ship `10.0.1` as a stable release from Spark.** This deliberately breaks the repo-wide lockstep:
  24 packages currently sit at `10.0.0-preview.80`, and this one alone moves to a stable `10.0.1`.
  The major stays `10` because the target framework is still `net10.0`, per the versioning rule in
  `CLAUDE.md`. Deprecate SlingBot's `10.0.0` on nuget.org pointing at `10.0.1`.
- **Keep MIT.** The licence differs from the Apache-2.0 `10.0.0`; MIT matches every other package in
  this repository and is intended. The Apache-2.0 build remains as a historical artifact.

---

## 3. Scope

### In scope

D1–D8, in a single pull request, per the one-PR rule.

The smee tunnel is consolidated: the raw-text SSE reader moves **into**
`MintPlayer.Spark.Webhooks.GitHub.DevTunnel`, CodeCoverage switches to `options.AddSmeeDevTunnel(...)`,
and `apps/CodeCoverage/CodeCoverage/Services/SmeeWebhookTunnelService.cs` is deleted. This drops the
`Smee.IO.Client` package reference entirely.

### Out of scope

- Porting `MintPlayer.DiffParser*` — deliberately abandoned (see header).
- Deprecating the `MintPlayer.SlingBot*` package IDs on nuget.org — a registry chore, not a code
  change; tracked separately.
- Any change to signature validation, GitHub App auth, or the production webhook path. The audit
  found those correct and, in two specific respects, stronger than SlingBot's.

---

## 4. Acceptance criteria

1. A smee-relayed webhook whose body contains a fractional-second timestamp with a non-local UTC
   offset passes HMAC verification and reaches its recipient.
2. The body handed to `WebhookEventProcessor.ProcessWebhookAsync` is byte-identical to the `body`
   span of the SSE frame, for a payload containing a fractional-second timestamp, a trailing-zero
   decimal (`1.50`), an integer beyond `long.MaxValue`, and an escaped solidus.
3. Verification in criterion 1 holds regardless of the host machine's timezone — asserted by running
   the test under at least two `TZ` settings.
4. `Smee.IO.Client` appears in no `.csproj` in the repository.
5. `apps/CodeCoverage/CodeCoverage/Services/SmeeWebhookTunnelService.cs` no longer exists, and
   CodeCoverage registers the tunnel through `options.AddSmeeDevTunnel(...)`.
6. With two dev WebSocket clients connected as different GitHub users, a webhook whose sender is
   user A is delivered to A's socket and not to B's.
7. The routing rule is a documented, overridable extension point with a default; the default is
   stated explicitly in the PR description.
8. `SocketClient.GitHubUsername` has at least one read.
9. `MintPlayer.Dotnet.SocketExtensions` declares `<Version>10.0.1</Version>` and
   `PackageLicenseExpression` MIT.
10. The smee tunnel reconnects after a dropped connection and does not throw out of `ExecuteAsync`
    on graceful shutdown.
11. No comment in the repository attributes the body corruption to smee.io pretty-printing, and no
    file references `docs/spark-handoff.md`.
12. The `.csproj.Backup.tmp` file is gone and the pattern is git-ignored.
13. Full solution build clean; full test suite green.

---

## 5. Risks

- **The end-to-end claim in criterion 1 is not yet measured against a real GitHub delivery.** The
  probe establishes that the object-model path corrupts and the raw-text path preserves; it does not
  prove a real signed payload verifies. The plan front-loads that as a spike, because if a real
  delivery still fails, the cause is upstream of us and the design changes.
- **D2's default routing rule is a behaviour change.** Broadcasting today means a developer relying
  on receiving a colleague's webhook would stop. Given `AllowedDevUsers` is dev-only and fails
  closed, the exposure is small, but the default must be chosen deliberately rather than inherited
  from SlingBot's demo.
- **Publishing a stable `10.0.1` breaks version lockstep** across the 24 packages. This is
  intentional and confined to one package, but CI packs solution-wide, so the version bump must be
  verified not to drag other packages to stable.
