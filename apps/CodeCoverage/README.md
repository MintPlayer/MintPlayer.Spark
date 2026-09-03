# Coverage

[![Coverage](https://coverage.mintplayer.com/badge/MintPlayer/MintPlayer.Spark.svg)](https://coverage.mintplayer.com/r/MintPlayer/MintPlayer.Spark)
[![CI](https://github.com/MintPlayer/MintPlayer.Spark/actions/workflows/pull-request.yml/badge.svg)](https://github.com/MintPlayer/MintPlayer.Spark/actions/workflows/pull-request.yml)
[![Deploy](https://github.com/MintPlayer/MintPlayer.Spark/actions/workflows/code-coverage-deploy.yml/badge.svg)](https://github.com/MintPlayer/MintPlayer.Spark/actions/workflows/code-coverage-deploy.yml)

A self-hosted code-coverage analyzer for GitHub — upload coverage reports from your
workflows, browse coverage per organization → repository → commit → file, and embed
badges in your READMEs.

Built on [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark)
(ASP.NET Core + RavenDB + Angular) with
[mintplayer-ng-bootstrap](https://github.com/MintPlayer/mintplayer-ng-bootstrap).

- **Product & architecture**: [docs/code-coverage/product-overview.md](../../docs/code-coverage/product-overview.md)
- **Milestone plan**: [docs/code-coverage/build-log-m0-m10.md](../../docs/code-coverage/build-log-m0-m10.md)
- **The upload GitHub Action**: [action/](action/README.md) (`uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@coverage-upload-v1`) — it lives beside the server it talks to, so an API change and the action change are one pull request
- **Releasing a new version of the action**: [action/README.md § Releasing](action/README.md#releasing-a-new-version) — Actions → *coverage-action-publish* → **Run workflow** with a `patch`/`minor`/`major` bump. The tag names derive from the action's `package.json`, so going to a new major needs no workflow edit
- **The upload API contract** (states, polling, gating a PR): [docs/code-coverage/upload-api.md](../../docs/code-coverage/upload-api.md)
- **Upstream (Spark) work items**: [docs/PRD-CoverageHandoff.md](../../docs/PRD-CoverageHandoff.md)
- **How this repo measures itself**: [docs/code-coverage/self-coverage-PRD.md](../../docs/code-coverage/self-coverage-PRD.md)

## Badges

`GET /badge/{owner}/{name}.svg` — deliberately unauthenticated, rate-limited per IP, cached for
300 s, and never a 404 (see below). Three variants:

| URL | Shows |
|---|---|
| `…/badge/{owner}/{name}.svg` | The repository headline — the newest **complete** assembly on the default branch. |
| `…?branch={ref}` | The newest covered commit of that branch. |
| `…?pr={number}` | The newest covered commit of that pull request. |

```markdown
[![Coverage](https://coverage.mintplayer.com/badge/MintPlayer/MintPlayer.Spark.svg?branch=feature/x)](https://coverage.mintplayer.com/r/MintPlayer/MintPlayer.Spark)
```

The repository page's badge panel has a branch picker that writes these snippets for you.

Things worth knowing before you rely on one:

- **It never 404s.** An unknown repository, an unknown branch, an unknown PR, a wrong token — all
  render the grey `unknown` badge at HTTP 200. A 404 would confirm that a private repository exists.
- **`coverage (partial)`** means the number came from a commit whose assembly is incomplete — a
  subset's total, not the repository's. The headline badge is only ever promoted from a complete
  assembly, so the label is how the two stay honest about each other.
- **`unknown` is not 0%.** It means nothing was measured (or nothing is visible to you).
- **A merged PR's badge goes `unknown`** once retention deletes that PR's builds.
- **Private repositories** need `?token={badge token}`, created and rotated on the repository page.
  The badge posted by the bot into a pull request uses a **per-PR signature** instead, so a comment
  never carries the repo-wide token.
- **Access control is exactly this**: public repositories are open; a private repository's badge
  needs its badge token; a private repository's PR badge needs the signature. The endpoint carries
  `[AllowAnonymous]` and no `[SparkAuthorize]`, so no `security.json` right governs it — deliberately,
  since `[AllowAnonymous]` wins over `SparkAuthorizeAttribute` and a declared-but-bypassed right
  would misrepresent the badge as gated.
- **On a repository with no App installation**, the branch and PR labels are asserted by whoever
  uploaded (`Commit.Branch` is first-writer-wins), so a branch badge there is only as trustworthy as
  that CI. With the App installed, the `pull_request` webhook is the authoritative writer. Measured
  across 71 pull-request head branches on the tracked MintPlayer repositories: no mislabelling
  observed; non-resolution was PRs predating coverage in their repository, plus dependabot PRs,
  which receive no repository secrets and so can never upload.

## How this repo measures itself

The badge above is this application reporting on its own source, through the same
API and the same action every other repository uses. Worth knowing if you are
setting up a repository that looks like this one:

- **Three reports, three flags, one build.** `.github/workflows/pull-request.yml` measures the
  .NET projects (coverlet → Cobertura), the Angular SPA and the action's TypeScript
  (Vitest → lcov) in separate jobs, then a single `coverage-upload` job uploads all
  three. Because the server keys a build on `(repository, commitSha, runId,
  runAttempt)` and merges sessions into it, `flags: dotnet` / `angular` / `action`
  give a number per language *and* one headline number. `finish: true` rides on the
  last of the three uploads — the action rejects an upload carrying no files, so
  there is no finish-only call to put at the end.
- **Nested workspaces must rebase their lcov paths.** Vitest writes `SF:` paths
  relative to its own project root, so `action/` and `Coverage/ClientApp/` both emit
  `src/main.ts`. The server resolves report paths against `git ls-files` by longest
  suffix and **drops ambiguous ones silently** — the report simply arrives smaller.
  `tools/rebase-lcov-paths.mjs` rewrites them to repo-root-relative and fails the
  job if any path names no tracked file. If your coverage looks inexplicably low
  after wiring up a monorepo, check this first.
- **The workflow uses `uses: ./apps/CodeCoverage/action`.** Consuming repositories should pin the
  moving major tag, `coverage-upload-v1`, as documented in [action/README.md](action/README.md).
  This one deliberately does not: uploading with the action *as the pull request changes it* is what
  catches a regression in the uploader before the next repository inherits it — and it is the reason
  the action's source lives here at all (see
  [coverage_action_home_PRD.md](../../docs/coverage_action_home_PRD.md)).
- **`code-coverage-deploy.yml` collects no coverage on purpose.** It also fires on a master push,
  and a second, .NET-only upload for the same commit would leave the badge showing
  whichever run finalized last. It is also filtered to ignore `apps/CodeCoverage/action/**`, so
  republishing the action's bundle never deploys the server.
- **A big negative `coverage/project` on a PR here is usually not a regression.** PR runs upload
  `partial: true` with an `nx affected` base, so the check is judged on the **scoped** basis — only
  the affected projects — while the benchmark is a whole-workspace master run. A PR touching one app
  can therefore read something like *"16.4% (−58.3% vs base 74.7%)"* while changing nothing about the
  rest of the repository. The check's own summary says which basis it used (*"Partial upload
  (nx affected) judged on the scoped basis"*), and the gate is informational here (`Blocking` off),
  so it never fails a merge. **Read `coverage/patch` instead** for whether a PR's own new lines are
  covered. This is also what the `coverage (partial)` badge label exists to make visible, and why the
  parameterised badges prefer a complete assembly over a newer partial one.

## Local development

Prerequisites:

- .NET 10 SDK, Node 22+
- RavenDB running unsecured on `http://localhost:8080` (the `Coverage` database is
  auto-created in Development)
- A GitHub App (for sign-in + webhooks). Follow the walkthrough in
  MintPlayer.Spark's `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/README.md`;
  for local webhook delivery use a [smee.io](https://smee.io) channel.

### GitHub App settings

Create one App per environment (dev + prod). What the app actually uses:

**Repository permissions**

| Permission | Level | Why |
|---|---|---|
| Contents | Read-only | File view fetches source at a commit through the installation token; also required to subscribe to `push` events |
| Metadata | Read-only | Mandatory on every App; covers repository listings |
| Pull requests | Read-only | Required to subscribe to `pull_request` events |

**Account permissions**

| Permission | Level | Why |
|---|---|---|
| Email addresses | Read-only | First-time sign-in only auto-provisions a local account when GitHub attests a **verified primary email** — Spark reads `GET /user/emails` with the user's token, and for a GitHub App that endpoint needs this permission. Without it the popup completes but sign-in fails with `email_not_verified`. |

No organization permissions are needed: the viewer's visibility is derived
from `GET /user/installations` with the **user's OAuth token**, which lists
whatever installations that user can access on their own authority.

Check-run feedback (`coverage/project` / `coverage/patch`, shipped with the
coverage-analyzer suite) and the **sticky pull-request comment** additionally
need **Checks: Read & write** and **Pull requests: Read & write**. Granting is a
two-step: raise the permissions on the App, then **each installation must accept
the change** before any check-run or comment appears (the
`new_permissions_accepted` webhook restores service automatically). Until an
installation accepts, its builds record `FeedbackState: Unavailable`-style
silence rather than errors, and a `403` on the comment is classified
`Unavailable` rather than retried — an unaccepted permission cannot be fixed by
trying again. Plain commit statuses are deliberately not used.

Verified 2026-09-03 for the `MintPlayer` org installation of the
`coverageproduction` App: `pull_requests: write` and `checks: write` are
**granted**, `repository_selection: all`, not suspended. (`emails: read` is
declared on the App but absent from that installation's grant — a live example
of the accepted-vs-declared gap this section warns about.)

**Webhook events to subscribe**: `Repository`, `Push`, `Pull request`
(`installation` / `installation_repositories` are always delivered to Apps, no
subscription needed). Webhook URL: your smee channel in dev, `https://<host>/api/github/webhooks` in prod;
set a webhook secret and keep it in `GitHub:WebhookSecret`.

**Identity (sign-in)**: add a **Callback URL** per environment — GitHub requires
exact matches including the port. Spark pins the OAuth callback path to
`/signin-github`, so for local dev that's `https://localhost:5200/signin-github`.
Leave *Request user authorization (OAuth) during installation* **unchecked**: it
makes GitHub redirect installs to the callback URL with a `code` but no OAuth
`state` (our server never initiated that flow), which the handler rejects. The
sign-in button performs its own properly-stated OAuth challenge and doesn't need
it. Optionally set the **Setup URL** to the app's home page so installs land
back in the app.
The App's *Client ID* / a generated *client secret* go into
`GitHub:{Development|Production}:ClientId` / `:ClientSecret` below. **These are
required to boot.** GitHub is the only authentication provider this app
registers, and Spark's local credentials are disabled, so a missing `ClientId`
means nobody could sign in at all — startup throws a named error naming the key
rather than serving an app whose sign-in button is broken. A fresh clone must
configure user-secrets before its first `dotnet run`.

The App also needs the **Email addresses: Read-only** account permission
(`user:email`); without it, first-time sign-in cannot resolve a user's address.

Configure secrets (never commit them):

```bash
cd Coverage
dotnet user-secrets set "GitHub:Development:ClientId" "Iv1.…"
dotnet user-secrets set "GitHub:Development:ClientSecret" "…"
dotnet user-secrets set "GitHub:Development:AppId" "123456"
dotnet user-secrets set "GitHub:Development:PrivateKeyPath" "C:/path/to/app.private-key.pem"
dotnet user-secrets set "GitHub:WebhookSecret" "…"
dotnet user-secrets set "GitHub:SmeeChannelUrl" "https://smee.io/your-channel"
```

Run:

```bash
dotnet run --project Coverage --launch-profile https
```

The host spawns the Angular dev server itself (SPA proxy middleware) — do **not** run
`ng serve` separately. App: https://localhost:5200.

After changing entities, regenerate the model metadata **and commit the result** —
`App_Data/Model/*.json` plus `App_Data/modelHashes.json`:

```bash
dotnet run --project Coverage --launch-profile Synchronize
```

The hash file is a startup gate: outside Development an app whose entity classes disagree
with its committed model **refuses to start**, so forgetting it breaks the deploy rather
than a page. It hashes the structural shape only — labels, renderers, groups, order and
visibility are hand-authored and deliberately excluded, so curating the model JSON never
invalidates it. To check without writing anything (this is what CI runs, exit 3 on drift):

```bash
dotnet run --project Coverage --launch-profile "Verify model"
```

Both commands return before the host is built, so they need no database and no free port —
they are safe to run while the app is running.

## Which HTTP surfaces you may build on

**`/api/uploads/*` is the public contract.** Uploading reports, finishing a build and reading a
run's result are documented in [docs/code-coverage/upload-api.md](../../docs/code-coverage/upload-api.md) and will stay compatible:
fields get added, never removed or repurposed. If you are automating anything — a merge gate, a
dashboard, a bot — this is the surface to use. It authenticates with an upload token or with GitHub
Actions OIDC, so it works for private repositories.

**`/api/browse/*` and `/spark/*` are internal to the web UI, with no compatibility promise.** They
exist to serve this app's own pages, they are unversioned and undocumented, and they are reshaped
whenever the UI is. They also cannot do what an automated caller needs: they authorize against a
signed-in human's GitHub access — so no CI credential can read a private repository through them —
and they answer `404` identically for "no data yet" and "not allowed". Building a gate on them will
work right up until it doesn't.

## Deployment

`docker-compose.yml` runs the app plus a pinned RavenDB on an internal network behind
Traefik. Every push to `master` tests, publishes `ghcr.io/mintplayer/codecoverage:master`,
and SSHes into the VPS to pull + restart (`.github/workflows/code-coverage-deploy.yml`). The VPS keeps
**no git checkout**: the deploy refetches `docker-compose.yml` from the repo each time,
while `.env` and `github-app.pem` in `/var/www/code-coverage` are **server-managed and never
touched by deploys**.

One-time VPS setup:

1. `mkdir -p /var/www/code-coverage`; copy `.env.example` there as `.env` and fill in the
   GitHub App credentials. The public hostname (`coverage.mintplayer.com`) is hardcoded
   in `docker-compose.yml` — Traefik Host rule and `Coverage__BaseUrl` (the OIDC
   audience) — like the other MintPlayer deployments; interpolating it from the server
   `.env` once produced a non-matching router and Traefik's default self-signed cert.
   Mind the `.env`'s line endings: it must be LF, a CRLF file poisons every value with
   an invisible `\r`.
2. Place the **production** GitHub App's private key at `/var/www/code-coverage/github-app.pem`,
   readable by the container's `app` user (UID 1654) — e.g. `chmod 644` or `chown 1654`.
   Beware: if the file is missing at first `up`, Docker silently creates a *directory*
   at that path and App auth fails at runtime.

   **Verify it is the right App's key** — a well-formed key for the *wrong* App (e.g. the
   development App's) authenticates nothing, and only App-authenticated calls notice:
   webhooks (secret) and uploads (`covt_`/OIDC) keep working while check-runs and
   `coverage.yml` loading silently fail. Compare against the fingerprint GitHub shows on
   the App's General page, without ever printing the key:

   ```bash
   openssl rsa -in github-app.pem -pubout -outform DER | openssl sha256 -binary | openssl base64
   ```

   **Replacing the key:** the compose file bind-mounts the single file, which binds the
   *inode* — `mv`/`scp` over it leaves the container reading the old file. Always
   `cat new.pem > github-app.pem`, then restart. `GET /health/ready` round-trips an App
   JWT to GitHub and returns 503 while the key is unusable (each deploy polls it), so a
   bad key now fails the deploy instead of surfacing hours later.

   **After fixing a bad key:** check-run publishing gives up per build after 5 attempts
   and never revisits it (`FeedbackState: Failed` is terminal) — a **new build** is
   required; existing failed builds will not retroactively get their check-runs.
3. `docker network create web` if it doesn't exist; Traefik must be attached to it, with
   an entrypoint named `websecure` and an ACME resolver named `letsencrypt` (the compose
   labels assume those exact names).
4. DNS A/AAAA record for the subdomain → the VPS, *before* the first deploy (Let's
   Encrypt won't issue without it).
5. GitHub side: repository secrets `VPS_HOST`, `VPS_USERNAME`, `VPS_SSH_KEY`
   (dedicated ed25519 deploy key in the VPS user's `authorized_keys`), optional
   `VPS_PORT` / `VPS_SSH_KEY_PASSPHRASE`. Verify the ghcr package is **public** after
   the first publish (the workflow's visibility PATCH is best-effort), or
   `docker login ghcr.io` on the VPS with a `read:packages` PAT.
6. Production GitHub App: callback URL `https://<host>/signin-github`, webhook URL
   `https://<host>/api/github/webhooks`, same permissions as the dev App.

Manual redeploy: the workflow's `workflow_dispatch` button, or on the VPS
`cd /var/www/code-coverage && docker compose pull && docker compose up -d --remove-orphans`
(always pull-then-up; the compose file has no build block by design).

RavenDB data lives in the `raven-data` named volume — it survives `pull`/`down`/`up`
deploys; only `docker compose down -v` or a volume prune destroys it. There is no
automated backup yet; back up the volume out-of-band if the data matters.
