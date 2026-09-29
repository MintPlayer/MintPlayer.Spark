# Coverage

[![Coverage](https://coverage.mintplayer.com/badge/github/MintPlayer/MintPlayer.Spark.svg)](https://coverage.mintplayer.com/github/r/MintPlayer/MintPlayer.Spark)
[![CI](https://github.com/MintPlayer/MintPlayer.Spark/actions/workflows/pull-request.yml/badge.svg)](https://github.com/MintPlayer/MintPlayer.Spark/actions/workflows/pull-request.yml)
[![Deploy](https://github.com/MintPlayer/MintPlayer.Spark/actions/workflows/code-coverage-deploy.yml/badge.svg)](https://github.com/MintPlayer/MintPlayer.Spark/actions/workflows/code-coverage-deploy.yml)

A self-hosted code-coverage analyzer for GitHub — upload coverage reports from your
workflows, browse coverage per organization → repository → commit → file, and embed
badges in your READMEs.

Built on [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark)
(ASP.NET Core + RavenDB + Angular) with
[mintplayer-ng-bootstrap](https://github.com/MintPlayer/mintplayer-ng-bootstrap).

- **Hosting your own instance**: [below](#hosting-your-own-instance), with every setting, default and secret in [docs/code-coverage/hosting.md](../../docs/code-coverage/hosting.md)
- **Product & architecture**: [docs/code-coverage/product-overview.md](../../docs/code-coverage/product-overview.md)
- **Milestone plan**: [docs/code-coverage/build-log-m0-m10.md](../../docs/code-coverage/build-log-m0-m10.md)
- **The upload GitHub Action**: [action/](action/README.md) (`uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@coverage-upload-v1`) — it lives beside the server it talks to, so an API change and the action change are one pull request
- **Releasing a new version of the action**: [action/README.md § Releasing](action/README.md#releasing-a-new-version) — Actions → *coverage-action-publish* → **Run workflow** with a `patch`/`minor`/`major` bump. The tag names derive from the action's `package.json`, so going to a new major needs no workflow edit
- **The upload API contract** (states, polling, gating a PR): [docs/code-coverage/upload-api.md](../../docs/code-coverage/upload-api.md)
- **Upstream (Spark) work items**: [docs/PRD-CoverageHandoff.md](../../docs/PRD-CoverageHandoff.md)
- **How this repo measures itself**: [docs/code-coverage/self-coverage-PRD.md](../../docs/code-coverage/self-coverage-PRD.md)

Contents:

- Using it: [Badges](#badges) · [How this repo measures itself](#how-this-repo-measures-itself) ·
  [Repositories that leave](#repositories-that-leave-connected-disconnected-deleted) ·
  [Which HTTP surfaces you may build on](#which-http-surfaces-you-may-build-on)
- [Hosting your own instance](#hosting-your-own-instance):
  [1. Requirements](#1-requirements) · [2. DNS](#2-dns) ·
  [3. The server](#3-the-server-docker-and-traefik) · [4. RavenDB](#4-ravendb) ·
  [5. The GitHub App](#5-the-github-app) · [6. Configuration](#6-configuration) ·
  [7. The deploy workflow](#7-the-deploy-workflow) · [8. Outgoing mail](#8-outgoing-mail-optional) ·
  [9. Operations](#9-operations) · [10. Our deployment](#10-our-deployment)
- [Local development](#local-development)

## Badges

`GET /badge/{provider}/{owner}/{name}.svg` — deliberately unauthenticated, rate-limited per IP, cached for
300 s, and never a 404 (see below). Three variants:

| URL | Shows |
|---|---|
| `…/badge/{provider}/{owner}/{name}.svg` | The repository headline — the newest **complete** assembly on the default branch. |
| `…?branch={ref}` | The newest covered commit of that branch. |
| `…?pr={number}` | The newest covered commit of that pull request. |

```markdown
[![Coverage](https://coverage.mintplayer.com/badge/github/MintPlayer/MintPlayer.Spark.svg?branch=feature/x)](https://coverage.mintplayer.com/github/r/MintPlayer/MintPlayer.Spark)
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
  relative to its own project root, so `action/` and `CodeCoverage/ClientApp/` both emit
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

## Repositories that leave: connected, disconnected, deleted

A repository can stop being reachable in five ways — transferred to an owner where the App is not
installed, deselected from the installation, the App suspended, the App uninstalled, or the
repository deleted on GitHub. **None of them destroys anything.** The repository is marked `Disconnected` with the reason, and:

- it disappears from account pages, repository grids and the owner's repo count, for everyone except
  someone who manages the owner;
- `/{provider}/r/{owner}/{name}`, the report pages and `/badge/{provider}/{owner}/{name}.svg` keep
  answering, with the
  coverage frozen at its last known value — README badges and links already posted in pull-request
  comments do not die with a transfer;
- a successful OIDC upload reconnects it, because a workflow that still runs is proof the repository
  is alive and ours. For a repository that moved to an org without the App, that is the only such
  proof available.

Renames and transfers are remembered in `PreviousFullNames`, so an old `owner/name` still resolves —
and if we never saw the rename, resolution falls back to asking GitHub, which keeps its own redirect
and answers with the numeric id. A *live* full name always wins over a remembered one, so a new
repository taking over an old name simply shadows the alias.

Suspension is called out separately from uninstalling because it is explicitly temporary: lifting it
restores every repository immediately, so the page says so rather than inviting you to press Delete
on data that is coming back.

Two mechanisms keep this true. Webhooks handle the timely case; a **nightly reconciler**
(`ReconcileGitHubStateCronJob`) asks each installation what it can actually see and repairs whatever
the webhooks missed — a dropped delivery, an outage, a change made while the app was down. The
**Resync** button on the accounts page runs the same reconciliation for the accounts you manage, and
any change to an installation triggers one for that account on the spot.

That last one is not belt-and-braces. Narrowing an installation from "all repositories" to a
selected few is reported by GitHub as an *addition*, with an empty removal list — the access lost to
every other repository on the account is announced nowhere. No webhook can detect it; only asking
GitHub what the installation now holds can. The same is true of an `unsuspend`, whose payload lists
no repositories at all.

Deleting the data is a separate, deliberate act: a disconnected repository's page offers a red
**Delete data**, available only to someone who manages the owner, which erases the repository and
every commit, build and coverage document beneath it. That is the only path in the application that
deletes coverage data.

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

---

## Hosting your own instance

The steps below are the order to do them in; the reference tables — every service, variable, key,
default, permission and secret, each cited to the file that sets it — are in
[docs/code-coverage/hosting.md](../../docs/code-coverage/hosting.md). Placeholders: `<your-domain>`
is the hostname the app will answer on, `<server-ip>` the server's public address.

The shape: one Linux host running Docker; a Traefik you already run terminates TLS and routes
`<your-domain>` to the app; the app, a pinned RavenDB and an optional outbound mail relay run from
one compose file; GitHub Actions builds the image and deploys it over SSH. **The server keeps no git
checkout**: each deploy re-downloads `docker-compose.yml` and pulls the image, and everything
secret or host-specific stays in files on the server that deploys never touch.

### 1. Requirements

| | |
|---|---|
| A Linux x64 server | The RavenDB image is the `-x64` build. Our deployment runs on a shared-vCPU VPS; the compose comments size the startup budget for that. |
| Docker Engine with the Compose v2 plugin | The deploy runs `docker compose`, not `docker-compose`. |
| Traefik v3, on an external Docker network named `web` | With an entrypoint named `websecure` and an ACME certificate resolver named `letsencrypt` — the compose labels use those exact names. Traefik runs as its own compose stack, outside this repository; [§3](#3-the-server-docker-and-traefik) shows the configuration it needs. |
| A hostname you control | For the app, and — only if you want mail — TXT records on its zone. |
| A GitHub App | One per environment ([§5](#5-the-github-app)). |
| A GitHub repository with Actions and GHCR | A fork of this one, to run the deploy workflow and publish the image. |
| Optional: a RavenDB licence | Without one Raven runs on its default AGPL terms; see [§4](#4-ravendb). |
| Optional: outbound port 25, or an SMTP provider | Only for mail ([§8](#8-outgoing-mail-optional)). |

⚠️ **The public hostname is hard-coded**, not configured: `docker-compose.yml` names
`coverage.mintplayer.com` in the Traefik `Host` rule, in `Coverage__BaseUrl` and in
`Spark__Auth__PublicBaseUrl`. A self-hosted instance edits those three lines in its fork. This is
deliberate — interpolating the host from the server `.env` once produced an empty or CRLF-poisoned
value, a router that never matched, and Traefik's default self-signed certificate. The mail defaults
(`MAIL_SENDER_DOMAIN`, `MAIL_HELO_HOSTNAME`) name the same host but *can* be overridden from `.env`.
The deploy workflow also hard-codes the image name `ghcr.io/mintplayer/codecoverage` and the server
directory `/var/www/code-coverage`.

### 2. DNS

| Record | Name | Value | When |
|---|---|---|---|
| A | `<your-domain>` | `<server-ip>` | **Before the first deploy** — Let's Encrypt will not issue without it. |
| AAAA | `<your-domain>` | the server's IPv6 address | Optional. If you publish one, make it the address the server actually *sends* from, or leave mail IPv4-only ([§8](#8-outgoing-mail-optional)). |
| PTR (reverse DNS, set at the hosting provider) | `<server-ip>` | `<your-domain>` | Only for direct mail delivery. Must match `MAIL_HELO_HOSTNAME`. |
| TXT (SPF) | `<your-domain>` | `v=spf1 ip4:<server-ip> -all` | Only for mail. |
| TXT (DKIM) | `<selector>._domainkey.<your-domain>` | `v=DKIM1;k=rsa;p=<public key>` | Only for mail. Selector defaults to `mail`. |
| TXT (DMARC) | `_dmarc.<parent domain>` | your policy | Check what the *parent* publishes: a subdomain inherits `sp=`. |

### 3. The server: Docker and Traefik

**Layout: one directory per compose stack.** Every service on the host is its own compose project
in its own directory under `/var/www/<name>` — Traefik in `/var/www/traefik`, this app in
`/var/www/code-coverage`. The directory name is the compose project name, so it prefixes the
volumes and networks (`code-coverage_raven-data`, `code-coverage_coverage-internal`). Stacks share
exactly one thing: the external `web` network, which every public stack joins and Traefik watches.

**Traefik**, as its own stack. What this app's labels need from it, as Traefik v3 command-line
arguments (the same settings can live in a static config file):

```yaml
# /var/www/traefik/docker-compose.yml (sketch — not part of this repository)
services:
  traefik:
    image: traefik:v3
    command:
      - --providers.docker=true
      - --providers.docker.exposedbydefault=false     # only containers with traefik.enable=true
      - --providers.docker.network=web
      - --entrypoints.web.address=:80
      - --entrypoints.web.http.redirections.entrypoint.to=websecure
      - --entrypoints.websecure.address=:443
      - --certificatesresolvers.letsencrypt.acme.httpchallenge.entrypoint=web
      - --certificatesresolvers.letsencrypt.acme.email=<your-email>
      - --certificatesresolvers.letsencrypt.acme.storage=/letsencrypt/acme.json
    ports: ["80:80", "443:443"]
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
      - letsencrypt:/letsencrypt
    networks: [web]
volumes:
  letsencrypt:
networks:
  web:
    external: true
```

- `exposedbydefault=false` is why the app's labels start with `traefik.enable=true`.
- The HTTP challenge runs on port 80, so 80 must be reachable from the internet even though every
  request is redirected to 443.
- `acme.json` holds the certificates' private keys: keep it on a volume, never print it.

One-time setup, in `/var/www/code-coverage` (the path the deploy workflow uses):

1. `mkdir -p /var/www/code-coverage`; copy `.env.example` there as `.env` and fill in the
   GitHub App credentials ([§6](#6-configuration)). Mind the `.env`'s line endings: it must be LF,
   a CRLF file poisons every value with an invisible `\r`.
2. `docker network create web` if it doesn't exist; Traefik must be attached to it, with
   an entrypoint named `websecure` and an ACME resolver named `letsencrypt` (the compose
   labels assume those exact names). The app joins `web` only so Traefik can reach it; RavenDB and
   the mail relay live on the project's own `coverage-internal` network and publish no host
   ports, which is what keeps the unsecured RavenDB and the unauthenticated relay off the internet.
3. The DNS A/AAAA record for the subdomain → the server, *before* the first deploy ([§2](#2-dns)).
4. Place the **production** GitHub App's private key at `/var/www/code-coverage/github-app.pem`,
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

   Replacing it later: see [§9](#replacing-the-github-app-key).
5. Optional: put the RavenDB licence at `/var/www/code-coverage/raven-license.json` ([§4](#4-ravendb)).
6. Optional, for mail: create `/var/www/code-coverage/mail-dkim` and follow
   [§8](#8-outgoing-mail-optional). Skipping this leaves the app unable to send,
   which is fine unless `ExternalLoginLinking` is `ConfirmByEmail` — Spark refuses to
   start in that combination rather than discarding confirmations silently.
7. Set up the GitHub side of the deploy ([§7](#7-the-deploy-workflow)) and push, or run the
   workflow by hand.

The resulting directory, all of it server-managed:

```
/var/www/code-coverage/
├── docker-compose.yml      # replaced by every deploy
├── .env                    # yours; deploys fail without it
├── github-app.pem          # yours; deploys fail without it
├── raven-license.json      # optional
└── mail-dkim/              # optional
    └── <your-domain>.private
```

### 4. RavenDB

`coverage-raven` runs the pinned `ravendb:7.1.10-ubuntu.22.04-x64` image, unsecured, on the
internal network only. It is pinned rather than `7.1-latest` because floating tags have previously
advanced to builds that reject unsecured access on a `0.0.0.0` bind, which broke deploys on a plain
`docker compose pull`. The app creates its `Coverage` database on first start
(`Spark__RavenDb__EnsureDatabaseCreated=true`). Data lives in the `raven-data` named volume.

#### RavenDB licence

Licensing is a **server-side** concern only: `Raven.Client`, and therefore the ASP.NET
Core app, has no notion of a licence and needs no configuration for this. Running without
one is supported — the server then reports `AGPL - Open Source` and is capped at 3 cores / 6 GB.

**A licence cannot be handed to this image as a setting.** That is the obvious approach
and it does not work: on `ravendb:7.1.10`, `RAVEN_License` (the JSON inline),
`RAVEN_License_Path`, `--License.Path` through `RAVEN_ARGS`, `RAVEN_SETTINGS`, and a
`License` object embedded in `settings.json` were each measured, and every one left the
server on `AGPL - Open Source`. There is no error, no warning and no log line for any of
them — a wrong licence, a malformed one and no licence at all are indistinguishable from
the outside. Do not re-introduce those settings; the compose file says the same.

What works is the activation endpoint, so `docker-compose.yml` carries a one-shot
`coverage-raven-license` service that POSTs the licence to `coverage-raven` once it is
healthy, then exits. The licence lands in the system database, inside the `raven-data`
volume — it survives restarts and `docker compose pull` + recreate, so this has to
succeed only once, and re-running it changes nothing. It runs on every `up` anyway, so a recreated
volume re-licenses itself instead of quietly reverting to AGPL.

To set it up:

1. Get the licence JSON — the licence mail from RavenDB, or *Studio → Settings → License*
   on a server that already has it. It looks like `{"Id":"…","Name":"…","Keys":["…"]}`.
2. Save it as `/var/www/code-coverage/raven-license.json`, next to `.env` and `github-app.pem`
   and server-managed in exactly the same way: deploys refetch `docker-compose.yml` but
   never touch this file. Pretty-printed is fine here — unlike a `.env` value, the file
   is read whole, so line breaks and CRLF are harmless. `apps/*/raven-license.json` is
   gitignored so a local copy cannot be committed.
3. `docker compose up -d`. Watch the activation actually happen — this is the step that
   tells you the licence was accepted, since RavenDB itself stays silent:

   ```bash
   docker compose logs coverage-raven-license
   docker compose ps -a coverage-raven-license   # want: Exited (0)
   ```

4. Confirm the server agrees. Raven publishes no host port, so ask from inside the
   network:

   ```bash
   docker compose run --rm --entrypoint sh coverage-raven-license -c \
     'curl -s http://coverage-raven:8080/license/status'
   ```

   `"Status":"Commercial"` with the expected `"Type"` means it took. `"Status":"AGPL -
   Open Source"` means it did not — check `raven-license.json` is a file and not an empty
   directory (Docker creates one if the path is missing when the container starts).

If `raven-license.json` is absent the POST fails, that one container exits non-zero, and the
deployment carries on unlicensed — `coverage-app` deliberately does not depend on it, so
a missing licence degrades the server rather than breaking the deploy.

The endpoint needs no credentials because `coverage-raven` is unsecured and reachable
only from the internal network. On a secured (HTTPS) RavenDB the same call requires a
client certificate.

#### What the Community licence limits

A free **Community** licence is enough for this app, but two of its limits are load-bearing, and
neither shows up in CI, which runs on a Developer licence that imposes neither:

- ⚠️ **Expiration and refresh have a 36-hour minimum period** (`MinPeriodForExpirationInHours` and
  `MinPeriodForRefreshInHours`, both 36, measured on production 2026-09-07). Spark's messaging
  host pins the expiration sweep to exactly 36 h for this reason
  (`libs/messaging/MintPlayer.Spark.Messaging/SparkMessagingExtensions.cs`). A smaller value is not
  degraded but **rejected** by the cluster, and the RavenDB process dies — it looks like "the server
  exited before becoming healthy", nothing like a licence problem. Never remove that pin.
- ⚠️ **3 data subscriptions per database.** Spark messaging now runs **one** subscription for all
  queues (`Spark:Messaging:SubscriptionMode = SingleSubscription`, the default). Before that, one
  subscription per queue exceeded the cap and five workers were silently dead in production — the
  create failed with `402 Payment Required`, and the app looked healthy
  (`CodeCoverage.Library/Feedback/CoverageQueues.cs`). It is also why a restore test must not import
  `Subscriptions` ([§9](#backing-up-the-database)).

A Community licence is renewed yearly; see [§9](#renewing-the-ravendb-licence).

### 5. The GitHub App

Create one App per environment (dev + prod). The full permission and event tables, with what each
one is derived from, are in [hosting.md §6](../../docs/code-coverage/hosting.md#6-github-app); what
the app actually uses:

**Repository permissions**

| Permission | Level | Why |
|---|---|---|
| Contents | Read-only; **Read & write** for branch deletion | File view fetches source at a commit through the installation token; also required to subscribe to `push` events. Deleting a merged pull request's head branch — opt-in per project board — needs write; with read the call is a 403 that is logged, not raised. |
| Metadata | Read-only | Mandatory on every App; covers repository listings |
| Pull requests | Read-only; **Read & write** for the PR comment | Required to subscribe to `pull_request` events |
| Checks | Read & write | The `coverage/project` and `coverage/patch` check-runs (see below) |
| Issues | Read-only | Only for project-board automation: the `issues` and `issue_comment` events |

**Account permissions**

| Permission | Level | Why |
|---|---|---|
| Email addresses | Read-only | First-time sign-in only auto-provisions a local account when GitHub attests a **verified primary email** — Spark reads `GET /user/emails` with the user's token, and for a GitHub App that endpoint needs this permission. Without it the popup completes but sign-in fails with `email_not_verified`. |

**Organization permissions**: none are needed for coverage itself. The
viewer's visibility is derived from `GET /user/installations` with the **user's OAuth token**, which
lists whatever installations that user can access on their own authority. Two optional features do
need one: **Projects: Read & write** for project-board automation (it moves cards through the
Projects V2 GraphQL API), and **Members: Read-only**, which GitHub requires in order to subscribe to
the `organization` event that reports organization renames.

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

**Webhook events to subscribe**: `Repository`, `Push`, `Pull request`, `Organization`; for
project-board automation also `Issues`, `Issue comment`, `Pull request review` and `Check run`
(`installation` / `installation_repositories` are always delivered to Apps, no
subscription needed). Webhook URL: your smee channel in dev, `https://<your-domain>/api/github/webhooks` in prod;
set a webhook secret and keep it in `GitHub:WebhookSecret` (`GITHUB_WEBHOOK_SECRET` on the server).

⚠️ An ungranted permission and "GitHub does not send this event" look identical from inside the
application: no error, no delivery, nothing in the log. A missing-event report starts on the App's
settings page, not in the code.

**Identity (sign-in)**: add a **Callback URL** per environment — GitHub requires
exact matches including the port. Spark pins the OAuth callback path to
`/signin-github`, so production is `https://<your-domain>/signin-github` and local dev is
`https://localhost:5200/signin-github`.
Leave *Request user authorization (OAuth) during installation* **unchecked**: it
makes GitHub redirect installs to the callback URL with a `code` but no OAuth
`state` (our server never initiated that flow), which the handler rejects. The
sign-in button performs its own properly-stated OAuth challenge and doesn't need
it. Optionally set the **Setup URL** to the app's home page so installs land
back in the app.

The App's *App ID*, *Client ID*, a generated *client secret* and a generated *private key* are what
the server needs: `GitHub:{Development|Production}:AppId` / `:ClientId` / `:ClientSecret` /
`:PrivateKeyPath` (in production: `GITHUB_APP_ID`, `GITHUB_APP_CLIENT_ID`,
`GITHUB_APP_CLIENT_SECRET` and the `github-app.pem` file). **The client id and secret are
required to boot.** GitHub is currently the only authentication provider this
app registers (see [multi-forge-PRD](../../docs/code-coverage/multi-forge-PRD.md)
for the work to add others), and Spark's local credentials are disabled, so a
missing `ClientId` means nobody could sign in at all — startup throws a named error naming the key
rather than serving an app whose sign-in button is broken. A fresh clone must
configure user-secrets before its first `dotnet run`.

**One webhook URL for two Apps (optional).** Set `GITHUB_DEVELOPMENT_APP_ID` on the production
server to the development App's id, and point the development App's webhook at production too:
production then forwards that App's deliveries to connected developer clients over a websocket
instead of processing them. Never set `GitHub:Development:AppId` on a developer machine — the
processor then skips every recipient.

### 6. Configuration

Everything the server needs is in `/var/www/code-coverage/.env`, which `docker-compose.yml`
interpolates. [`.env.example`](.env.example) lists every variable with its caveats; the complete
tables — variable, what it maps to, default, required or not, meaning — are in
[hosting.md §2](../../docs/code-coverage/hosting.md#2-server-env), and every application and Spark
configuration key behind them is in [§4](../../docs/code-coverage/hosting.md#4-application-configuration-keys)
and [§5](../../docs/code-coverage/hosting.md#5-spark-framework-keys).

The minimum to boot:

| Variable | What it is |
|---|---|
| `GITHUB_WEBHOOK_SECRET` | The App's webhook secret. |
| `GITHUB_APP_ID` | The App's numeric id. |
| `GITHUB_APP_CLIENT_ID` | OAuth client id — startup throws without it. |
| `GITHUB_APP_CLIENT_SECRET` | OAuth client secret. |

Worth setting: `COVERAGE_BADGE_SIGNING_KEY` (otherwise private repositories' PR comments show text
instead of a badge) and `GITHUB_APP_SLUG` (for "install the App" links). The mail variables are all
optional ([§8](#8-outgoing-mail-optional)).

Three things that are **not** in `.env`:

- **The hostname** — hard-coded in `docker-compose.yml` ([§1](#1-requirements)).
- **The RavenDB licence** — a file, because RavenDB ignores it as a setting ([§4](#4-ravendb)).
- **`ASPNETCORE_ENVIRONMENT=Production`** — set in the compose file, and load-bearing:
  `Program.cs` derives the `GitHub:{environment}:*` key prefix from the environment name, so any
  other value silently reads different keys and disables sign-in.

⚠️ `Coverage__BaseUrl` is three things at once: the base of every link the app builds, the **OIDC
audience** CI workflows must request (the action passes its `url` input, slash-stripped), and the
passkey relying-party id. Keep it without a trailing slash, and do not change it once users have
enrolled passkeys — see [Local development](#local-development) for why a passkey never moves.

### 7. The deploy workflow

`docker-compose.yml` runs the app plus a pinned RavenDB on an internal network behind
Traefik. Every push to `master` that touches the app or a library it depends on builds and publishes
`ghcr.io/mintplayer/codecoverage:master`,
and SSHes into the VPS to pull + restart (`.github/workflows/code-coverage-deploy.yml`). The workflow
runs no tests itself — `pull-request.yml` gates those before the merge. The VPS keeps
**no git checkout**: the deploy refetches `docker-compose.yml` from the repo each time,
while `.env` and `github-app.pem` in `/var/www/code-coverage` are **server-managed and never
touched by deploys**.

GitHub side, once:

1. Repository secrets `VPS_HOST`, `VPS_USERNAME`, `VPS_SSH_KEY`
   (dedicated ed25519 deploy key in the VPS user's `authorized_keys`), optional
   `VPS_PORT` / `VPS_SSH_KEY_PASSPHRASE`. The workflow also reads `NX_CACHE_SERVER`,
   `NX_CACHE_RW_TOKEN` and `NX_CACHE_RO_TOKEN` for the Nx remote cache; they are optional, and the
   image build deliberately does not use them.
2. Verify the ghcr package is **public** after
   the first publish (the workflow's visibility PATCH is best-effort). Deploys log in to ghcr with
   the job's own token, so they work either way, but a manual `docker compose pull` on the server
   needs a public package or `docker login ghcr.io` with a `read:packages` PAT.
3. Production GitHub App: callback URL `https://<your-domain>/signin-github`, webhook URL
   `https://<your-domain>/api/github/webhooks`, same permissions as the dev App ([§5](#5-the-github-app)).

What a deploy does on the server: fail unless `.env` and `github-app.pem` exist; download
`docker-compose.yml` from `master`; `docker compose pull`, `down --remove-orphans`,
`up -d --remove-orphans` (so there is a short outage); prune images; then poll `/health/ready` for
up to 660 s — `coverage-app`'s 600 s start period plus a minute of slack. That is a failure bound,
not a wait: the first 200 ends it. `/health/ready` round-trips an App JWT to GitHub, so a wrong key **fails the deploy**
(503) instead of surfacing hours later in check-runs. The triggering paths, secrets and steps are
listed in full in [hosting.md §7](../../docs/code-coverage/hosting.md#7-deploy-workflow).

Manual redeploy: the workflow's `workflow_dispatch` button, or on the VPS
`cd /var/www/code-coverage && docker compose pull && docker compose up -d --remove-orphans`
(always pull-then-up; the compose file has no build block by design).

### 8. Outgoing mail (optional)

The app sends exactly one kind of message: the "do you want this login attached to your
account?" confirmation, and only when `Spark__Auth__ExternalLoginLinking=ConfirmByEmail`.
It is **off by default** — there is no second forge worth linking to yet — so a deployment
that leaves `MAIL_FROM_ADDRESS` unset registers no mail transport at all, which is a
supported state. The general guide, with bounces and provider fallback, is
[docs/guide-outgoing-mail.md](../../docs/guide-outgoing-mail.md).

**The app never talks to the internet.** It hands the message to the `coverage-smtp`
container (`boky/postfix:v4.3.0`) on the internal network and returns; that container queues it
(on the `smtp-queue` volume, so a redeploy loses nothing) and does the
delivering. This is not tidiness: the send happens *inside the external-login callback*,
while somebody is waiting on an HTTP response, and a handoff one hop away takes
milliseconds whether or not the receiving mail server is reachable.

To turn it on: set `MAIL_FROM_ADDRESS` (and `MAIL_SENDER_DOMAIN` / `MAIL_HELO_HOSTNAME` if your
domain is not the default), publish the DNS records below, put the DKIM key in place, and redeploy.

⚠️ **Delivery is the hard part, not the wiring.** All of the following was measured
against this VPS on 2026-09-21, and every step of it was necessary:

| | |
|---|---|
| Hetzner blocks outbound **25 and 465** on all Cloud Servers | Request an unblock from the Hetzner console (Limits page). 587 is open by default, so relaying through a provider needs no request. |
| Reverse DNS must match the HELO name | Set the PTR to `coverage.mintplayer.com` in the Hetzner console. It forward-resolves to the server, which is what receivers check. **Done for IPv4 on 2026-09-29**. The IPv6 address has no PTR, which is why the relay is IPv4-only (below). |
| `mintplayer.com` publishes DMARC **`sp=reject`** | Every subdomain inherits it. Mail that fails alignment is **rejected**, not junked — measured: `550 5.7.509 ... does not pass DMARC verification and has a DMARC policy of reject`. |

Two DNS records make it pass, both on `mintplayer.com`'s zone:

```
coverage                    TXT   v=spf1 ip4:188.245.190.60 ip6:2a01:4f8:c0c:f87c:: -all
mail._domainkey.coverage    TXT   v=DKIM1;k=rsa;p=<public key>
```

⚠️ **The relay sends over IPv4 only** (`POSTFIX_inet_protocols=ipv4` in the compose file).
IPv6 fails, as measured on 2026-09-29:
- **SPF:** the VPS sends IPv6 from `2a01:4f8:c0c:f87c::1`, but the SPF record's `ip6:` term and the
  AAAA both name `2a01:4f8:c0c:f87c::`, which is `::0`, a different address. So an IPv6 send
  fails SPF.
- **Reverse DNS:** `::1` has no PTR, and Gmail and Outlook refuse IPv6 senders without one.

`smtp_address_preference=ipv4` alone was not enough, because it only prefers IPv4.

To re-enable IPv6 later, all three have to change together: the SPF term must become
`ip6:2a01:4f8:c0c:f87c::/64` (or `::1`), the address needs a PTR to `coverage.mintplayer.com`,
and the AAAA must name the sending address.

Generating the DKIM key, on the VPS:

```bash
cd /var/www/code-coverage
mkdir -p mail-dkim && cd mail-dkim
docker run --rm -v "$PWD":/out alpine:3.20 sh -c \
  'apk add --no-cache opendkim-utils && opendkim-genkey -b 2048 \
     -d coverage.mintplayer.com -s mail -D /out'
mv coverage.mintplayer.com/mail.private coverage.mintplayer.com.private   # see below
chown -R 101:104 . && chmod 600 *.private                                 # see below
```

⚠️ **Two layout traps, each of which cost a deploy cycle:**

1. **The key must be flat** — `mail-dkim/<domain>.private`. `opendkim-genkey` writes
   `<domain>/<selector>.private`, and with that layout the image logs `Skipping DKIM` and
   delivers the message **unsigned**. It still arrives, because SPF passes on its own, so
   the failure is invisible unless you read the container log or the message headers.
2. **The key must be owned by opendkim on the host** (`101:104` in `boky/postfix:v4.3.0`).
   The image tries to `chown` a key it cannot read, the read-only mount refuses, and the
   container exits — which at least fails loudly, unlike the first trap.

Verify the key against what is published, which is the check that actually proves it:

```bash
docker run --rm -v /var/www/code-coverage/mail-dkim:/keys:ro alpine:3.20 sh -c \
  'apk add --no-cache opendkim-utils bind-tools >/dev/null &&
   opendkim-testkey -d coverage.mintplayer.com -s mail \
     -k /keys/coverage.mintplayer.com.private -vvv'
```

`key OK` means the private key and the DNS record agree. Anything else means the record
was pasted wrong — most often line-wrapping inside the base64.

**If deliverability ever degrades**, set `MAIL_RELAY_HOST` (plus username and password)
to a provider's submission host. Postfix then relays instead of delivering directly, the
port 25 unblock stops mattering, and the provider's reputation replaces this IP's.

### 9. Operations

#### Volumes: what survives what

| Named volume | Holds | Losing it means |
|---|---|---|
| `raven-data` | The database **and** the activated RavenDB licence (system database) | Everything. Back it up ([below](#backing-up-the-database)). |
| `dataprotection-keys` | The Data Protection key ring | Every user is signed out once and outstanding antiforgery tokens are invalidated. Nothing else. |
| `smtp-queue` | Mail queued for delivery | Undelivered queued mail. |

On disk they are prefixed with the project name (`code-coverage_raven-data`, …); a volume is
created by the first `up` that declares it. All three survive `pull`/`down`/`up` deploys; only `docker compose down -v` or a volume prune
destroys them. The files next to the compose file ([§3](#3-the-server-docker-and-traefik)) are not
in any volume — back up `.env`, `github-app.pem` and `raven-license.json` separately, somewhere
private.

#### Data Protection keys

The key ring decrypts authentication and antiforgery cookies. Spark refuses to start in Production
without a location for it; the compose file points `Spark__DataProtection__KeysPath` at the
`dataprotection-keys` volume, and the Dockerfile pre-creates that mount point owned by the `app`
user, so a fresh volume is writable by the non-root process. `appsettings.json` deliberately sets no
`Storage`, because `KeysPath` and `Storage=RavenDb` together also refuse startup. Changing
`Spark:DataProtection:ApplicationName` (`CodeCoverage`) or the key location signs every user out
once.

**First deploy with the volume signs everyone out once.** The ring used to live in RavenDB as
`DataProtectionKeys/` documents; moving to `KeysPath` starts a new ring. Those documents are
orphaned by the move and can be deleted once the first deploy with the volume is healthy
(`Program.cs`).

#### Replacing the GitHub App key

The compose file bind-mounts the single file, which binds the
*inode* — `mv`/`scp` over it leaves the container reading the old file. Always
`cat new.pem > github-app.pem`, then restart. `GET /health/ready` round-trips an App
JWT to GitHub and returns 503 while the key is unusable (each deploy polls it), so a
bad key now fails the deploy instead of surfacing hours later.

**After fixing a bad key:** check-run publishing gives up per build after 5 attempts
and never revisits it (`FeedbackState: Failed` is terminal) — a **new build** is
required; existing failed builds will not retroactively get their check-runs.

#### Backing up the database

⚠️ **There is still no *automated* backup.** The procedure below is manual, and was run and
verified end to end on 2026-09-21; RavenDB reported `BackupInfo: null` before it, meaning the
database had never been backed up at all. Run it before anything irreversible — a migration, a
re-key, a bulk delete, a RavenDB upgrade.

```bash
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
DIR=/var/backups/coverage-raven && mkdir -p "$DIR"
NET=code-coverage_coverage-internal

# Stats first: this is what the restore gets checked against.
docker run --rm --network $NET --user 0 -v "$DIR":/out curlimages/curl:8.11.1 \
  -sS "http://coverage-raven:8080/databases/Coverage/stats" -o "/out/coverage-$STAMP.stats.json"

docker run --rm --network $NET --user 0 -v "$DIR":/out curlimages/curl:8.11.1 \
  -sS -f --max-time 3600 \
  -X POST "http://coverage-raven:8080/databases/Coverage/smuggler/export" \
  -H "Content-Type: application/json" \
  --data '{"OperateOnTypes":"Documents,RevisionDocuments,Indexes,Identities,CompareExchange,Attachments,CounterGroups,Subscriptions,TimeSeries","IncludeExpired":true}' \
  -o "/out/coverage-$STAMP.ravendbdump"
```

`NET` is `<compose project>_coverage-internal`, and the project name is the directory name — so
this value holds for `/var/www/code-coverage`.

⚠️ `CompareExchange` is not optional. The Identity **email reservations** live in compare-exchange,
not in any collection, so a dump without it restores a database in which every existing address can
be registered again.

⚠️ `--user 0` is needed because `curlimages/curl` runs unprivileged and cannot write into a
root-owned bind mount. It fails as `curl: (23) client returned ERROR on write`, which names neither
permissions nor the directory.

**Three things about verifying it**, each of which produced a misleading answer first:

1. The dump is **Zstandard**, not gzip — `gzip -t` reports "not in gzip format" on a perfectly good
   file. Use `zstd -t`.
2. Grepping the decompressed stream for attachment markers finds **nothing**, because attachments
   are not a top-level section. That is not evidence of absence.
3. The only check that settles it is a **restore**. Import into a scratch database and compare
   `CountOfDocuments` and `CountOfAttachments` against the stats file captured above:

```bash
docker run --rm --network $NET --user 0 curlimages/curl:8.11.1 -sS \
  -X PUT "http://coverage-raven:8080/admin/databases?name=CoverageRestoreTest&replicationFactor=1" \
  -H 'Content-Type: application/json' --data '{"DatabaseName":"CoverageRestoreTest"}'

docker run --rm --network $NET --user 0 -v "$DIR":/b curlimages/curl:8.11.1 -sS -f --max-time 3600 \
  -X POST "http://coverage-raven:8080/databases/CoverageRestoreTest/smuggler/import" \
  -F 'importOptions={"OperateOnTypes":"Documents,RevisionDocuments,Identities,CompareExchange,Attachments,CounterGroups,TimeSeries"}' \
  -F "file=@/b/coverage-$STAMP.ravendbdump"

# ... compare stats, then hard-delete the scratch database ...
docker run --rm --network $NET --user 0 curlimages/curl:8.11.1 -sS \
  -X DELETE "http://coverage-raven:8080/admin/databases?name=CoverageRestoreTest&hard-delete=true" \
  -H 'Content-Type: application/json' \
  --data '{"DatabaseNames":["CoverageRestoreTest"],"HardDelete":true}'
```

⚠️ Exclude `Subscriptions` and `Indexes` from the *restore test* import. The Community licence caps
subscriptions cluster-wide, so importing the live database's could disturb production; and
re-indexing 228k documents burns CPU on the production host for nothing, since the index
*definitions* are confirmed present in the dump either way.

Finally, **copy the dump off the server** and compare `sha256sum` at both ends. A backup on the
same disk as the data protects against a bad migration, which is the common case, but not against
losing the disk.

##### Automating it (recommended)

Nothing in this repository schedules a backup, and a manual procedure only runs when someone
remembers. Pick at least one of these, and preferably a database-level one *and* a host-level one:

- **A RavenDB periodic backup task** on the `Coverage` database (Studio → *Tasks → Backups*, or the
  `/admin/periodic-backup` API), writing to a directory that is itself copied off the host. Check
  first that your licence tier offers the destination you want: `/license/status` lists the
  features it grants.
- **The export above, on a schedule** — a cron entry or systemd timer running the two `curl`
  commands, followed by an off-host copy. It is the procedure already verified end to end.
- **A volume snapshot** of `code-coverage_raven-data`. Stop `coverage-raven` for the copy, or the
  snapshot is only crash-consistent.
- **Provider-level server backups** (Hetzner Cloud offers daily whole-server backups as a paid
  option). Whole-disk and crash-consistent, so a complement to the above, not a replacement.

How to check what is in place, since none of it announces itself:

```bash
crontab -l; ls /etc/cron.d; systemctl list-timers --no-pager      # host-level schedules
docker run --rm --network code-coverage_coverage-internal curlimages/curl:8.11.1 -sS \
  "http://coverage-raven:8080/databases/Coverage/tasks"            # look for a Backup task
```

Provider backups are visible only in the provider's console (for Hetzner: the server's *Backups*
tab). Whatever you choose, prove it the same way as above — by restoring one.

#### Renewing the RavenDB licence

A Community licence runs for a year. Renewing it emails a **new** licence JSON, and until that one
is activated the server keeps the old one — and falls back to AGPL when it expires, silently, as
with every other licence failure.

1. Replace the file's contents in place — `cat new-licence.json > raven-license.json`, not
   `mv`/`scp`, for the same inode reason as the App key.
2. Re-run the one-shot: `docker compose up -d --force-recreate coverage-raven-license` (any deploy
   also runs it).
3. Check its log and exit code, then `/license/status`, exactly as in
   [§4](#ravendb-licence) steps 3–4.

#### Upgrading

- **The app** follows `master`: merging is deploying. Pending `ISparkMigration`s run at startup,
  before the app listens, under a cluster-wide lock. That is why `coverage-app`'s healthcheck
  `start_period` is 600 s: a re-key migration was measured at roughly 100 s on a restored production
  copy (224k documents plus 708 attachment moves), and a container still migrating must not be
  marked unhealthy. It is a start period, not a timeout, so it costs nothing on an ordinary deploy.
  The deploy's readiness bound (660 s) is sized from it; keep the two in step.
- **`stop_grace_period: 120s`** lets an in-flight message handler finish on shutdown. A handler
  killed mid-flight is not lost — its message is reclaimed after `ClaimTtl` (5 min) — but it is
  delayed and partly re-run. Keep the grace period below `ClaimTtl`.
- **The model hash is a startup gate**: outside Development an image whose entity classes disagree
  with its committed `App_Data/modelHashes.json` refuses to start ([Local development](#local-development)).
- **RavenDB** is pinned. Upgrading it is a deliberate edit of the image tag in
  `docker-compose.yml`: back up first, and check that the new build still allows unsecured access on
  a `0.0.0.0` bind — the reason the tag is pinned at all.
- **The relay and the one-shot** (`boky/postfix`, `curlimages/curl`) are pinned the same way. The
  DKIM key ownership (`101:104`) is specific to `boky/postfix:v4.3.0`; recheck it on an upgrade.

### 10. Our deployment

The public facts of coverage.mintplayer.com, as a worked example of the steps above. Host facts
were read on 2026-09-29.

| | |
|---|---|
| Host | A Hetzner Cloud **CPX32** (shared vCPU) in Nuremberg, running Debian GNU/Linux 13 (trixie), Docker Engine 29.3.0 and Docker Compose 5.1.1. |
| Layout | Twelve compose stacks, one directory each under `/var/www/<name>`. This one is `/var/www/code-coverage`, deployed by `code-coverage-deploy.yml` on every qualifying push to `master`. |
| Traefik | 3.6.11 (image `traefik:latest`), its own stack in `/var/www/traefik`, configured exactly as the sketch in [§3](#3-the-server-docker-and-traefik): Docker provider with `exposedbydefault=false` on network `web`, `web` (:80) redirecting to `websecure` (:443), resolver `letsencrypt` using the HTTP challenge, certificates on a volume. It also loads a file provider from a host directory (`/var/www/traefik/dynamic`, watched) for routes that are not containers, and a certificate-dumper container runs beside it. Neither matters to this app. |
| Networks | `web` (external, shared by every public stack), `code-coverage_coverage-internal`, and Traefik's own default network. |
| Server-managed files | `.env`, `github-app.pem`, `raven-license.json` and `mail-dkim/` (the flat `coverage.mintplayer.com.private`, plus the nested copy `opendkim-genkey` wrote, which the relay ignores). |
| `.env` | Sets `GITHUB_WEBHOOK_SECRET`, `GITHUB_APP_ID`, `GITHUB_APP_CLIENT_ID`, `GITHUB_APP_CLIENT_SECRET` and `COVERAGE_BADGE_SIGNING_KEY`. It also still carries a `TRAEFIK_HOST`, a leftover that nothing reads since the hostname was hard-coded. |
| Volumes | `code-coverage_raven-data` and `code-coverage_smtp-queue`. `code-coverage_dataprotection-keys` is created by the first deploy that declares it — that deploy signs everyone out once ([§9](#data-protection-keys)). |
| DNS | The `mintplayer.com` zone is hosted separately from the server, at a Plesk-based DNS host; `coverage` is a record in that zone. `mintplayer.com`'s own MX points at a third-party spam filter, which plays no part in this app's outgoing mail. |
| A / AAAA | `coverage.mintplayer.com` → `188.245.190.60` / `2a01:4f8:c0c:f87c::` |
| SPF | `coverage.mintplayer.com TXT "v=spf1 ip4:188.245.190.60 ip6:2a01:4f8:c0c:f87c:: -all"` |
| DKIM | Selector `mail` — `mail._domainkey.coverage.mintplayer.com`, a 2048-bit RSA key. |
| DMARC | None of its own. `_dmarc.mintplayer.com` is a CNAME to the DNS host's default policy, `p=none; sp=reject`, so `coverage.mintplayer.com` inherits **`sp=reject`**. |
| Reverse DNS | The IPv4 address's PTR is `coverage.mintplayer.com` (set 2026-09-29). The IPv6 address has **no** PTR, and it sends from `…::1` rather than the published `…::` — which is why outgoing mail is IPv4-only. |
| Outbound SMTP | Port 25 open (Hetzner unblocked 25 and 465 on request). |
| Mail | **Disabled.** No `MAIL_*` variables are set in the server's `.env`, so the app registers no mail transport; the relay container runs but receives nothing. `ExternalLoginLinking` is `Disabled`. |
| RavenDB | `ravendb:7.1.10-ubuntu.22.04-x64` with a **Community** licence, activated through `coverage-raven-license`. It is renewed yearly: the renewal emails a new licence, which must then be activated ([§9](#renewing-the-ravendb-licence)). |
| Backups | ⚠️ **None scheduled.** No cron entry or systemd timer backs up `code-coverage_raven-data`; the manual export in [§9](#backing-up-the-database) is the only backup that has been made. Whether Hetzner server backups are enabled is not recorded here — check the server's *Backups* tab. See [Automating it](#automating-it-recommended). |
| GitHub | Two Apps, `coverageproduction` and `coveragedevelopment`; the production App's webhook URL is `https://coverage.mintplayer.com/api/github/webhooks`. |

---

## Local development

Prerequisites:

- .NET 11 SDK (the solution and the Dockerfile target `net11.0`), Node 22+
- RavenDB running unsecured on `http://localhost:8080` (the `Coverage` database is
  auto-created in Development)
- A GitHub App (for sign-in + webhooks) — a **separate development App**, set up as in
  [§5](#5-the-github-app). The walkthrough in MintPlayer.Spark's
  `libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/README.md` covers the basics;
  for local webhook delivery use a [smee.io](https://smee.io) channel.

Configure secrets (never commit them):

```bash
cd apps/CodeCoverage/CodeCoverage
dotnet user-secrets set "GitHub:Development:ClientId" "Iv1.…"
dotnet user-secrets set "GitHub:Development:ClientSecret" "…"
dotnet user-secrets set "GitHub:Development:AppId" "123456"
dotnet user-secrets set "GitHub:Development:PrivateKeyPath" "C:/path/to/app.private-key.pem"
dotnet user-secrets set "GitHub:WebhookSecret" "…"
dotnet user-secrets set "GitHub:SmeeChannelUrl" "https://smee.io/your-channel"
```

Run:

```bash
dotnet run --project apps/CodeCoverage/CodeCoverage --launch-profile https
```

The host spawns the Angular dev server itself (SPA proxy middleware) — do **not** run
`ng serve` separately. App: https://localhost:5200.

⚠️ **Use the `https` profile for passkeys too** — because GitHub sign-in is the only way to get the
session that passkey enrollment requires, not because of WebAuthn itself. `http://localhost` *is* a
secure context by the Secure Contexts spec, so the passkey UI does render on the `http` profile; you
simply have no way to sign in first.

A passkey enrolled in development binds to `localhost` and **will never work against production**.
That is by design: a passkey is bound to its relying-party id for life, and `PasskeyServerDomain` is
pinned from `Coverage:BaseUrl`, which differs between the two.

After changing entities, regenerate the model metadata **and commit the result** —
`App_Data/Model/*.json` plus `App_Data/modelHashes.json`:

```bash
dotnet run --project apps/CodeCoverage/CodeCoverage --launch-profile Synchronize
```

The hash file is a startup gate: outside Development an app whose entity classes disagree
with its committed model **refuses to start**, so forgetting it breaks the deploy rather
than a page. It hashes the structural shape only — labels, renderers, groups, order and
visibility are hand-authored and deliberately excluded, so curating the model JSON never
invalidates it. To check without writing anything (this is what CI runs, exit 3 on drift):

```bash
dotnet run --project apps/CodeCoverage/CodeCoverage --launch-profile "Verify model"
```

Both commands return before the host is built, so they need no database and no free port —
they are safe to run while the app is running.
