# CodeCoverage — hosting reference

The lookup tables behind the hosting guide in
[`apps/CodeCoverage/README.md`](../../apps/CodeCoverage/README.md#hosting-your-own-instance). The
guide says *what to do in which order*; this file says *what every knob is, what it defaults to, and
where that default comes from*. Every default below is cited to the file that sets it — if a table
and its file disagree, the file wins and the table is the bug.

Placeholders: `<your-domain>` is the public hostname the app answers on, `<server-ip>` the host's
public address.

Contents:

1. [Compose services](#1-compose-services)
2. [Server `.env`](#2-server-env)
3. [Files next to the compose file](#3-files-next-to-the-compose-file)
4. [Application configuration keys](#4-application-configuration-keys)
5. [Spark framework keys](#5-spark-framework-keys)
6. [GitHub App](#6-github-app)
7. [Deploy workflow](#7-deploy-workflow)
8. [The upload action](#8-the-upload-action)
9. [Fixed in code](#9-fixed-in-code)
10. [Inconsistencies found](#10-inconsistencies-found)

---

## 1. Compose services

Source: [`apps/CodeCoverage/docker-compose.yml`](../../apps/CodeCoverage/docker-compose.yml). No
service publishes a host port. Traefik reaches the app over the external `web` network; everything
else is on the project's own `coverage-internal` bridge network. Traefik itself is a separate stack;
what it must provide (Docker provider on `web`, `exposedbydefault=false`, entrypoints `web` →
`websecure`, resolver `letsencrypt`) is sketched in the README's
[§3](../../apps/CodeCoverage/README.md#3-the-server-docker-and-traefik).

| Service | Image (pinned tag) | Networks | Volumes | Healthcheck | Restart | Role |
|---|---|---|---|---|---|---|
| `coverage-raven` | `docker.io/ravendb/ravendb:7.1.10-ubuntu.22.04-x64` | `coverage-internal` | `raven-data` → `/var/lib/ravendb/data` | TCP connect to `127.0.0.1:8080`; interval 10 s, timeout 5 s, retries 12, start period 30 s | `unless-stopped` | The database, unsecured, reachable only from the internal network. |
| `coverage-raven-license` | `curlimages/curl:8.11.1` | `coverage-internal` | `./raven-license.json` → `/raven-license.json` (ro) | — | `"no"` (one-shot) | POSTs the licence to `http://coverage-raven:8080/admin/license/activate` once Raven is healthy, with 10 retries 3 s apart, then exits. Runs on every `up`. Nothing depends on it. |
| `coverage-smtp` | `boky/postfix:v4.3.0` | `coverage-internal` | `./mail-dkim` → `/etc/opendkim/keys` (ro); `smtp-queue` → `/var/spool/postfix` | TCP connect to `127.0.0.1:587` (under `bash`: `/dev/tcp` does not exist in the image's `sh`, which is dash); interval 15 s, timeout 5 s, retries 6, start period 20 s | `unless-stopped` | Outbound-only mail relay. Unauthenticated, which is safe only because nothing outside the project can reach it. |
| `coverage-app` | `ghcr.io/mintplayer/codecoverage:master` (floating, pull-only: no `build:` block) | `web`, `coverage-internal` | `./github-app.pem` → `/run/secrets/github-app.pem` (ro); `dataprotection-keys` → `/var/lib/codecoverage/dataprotection-keys` | `GET /health` must answer 200; interval 15 s, timeout 5 s, retries 8, **start period 600 s** | `unless-stopped` | The application. `stop_grace_period: 120s`. Waits for `coverage-raven` to be *healthy* and `coverage-smtp` to be *started*. |

Named volumes: `raven-data`, `smtp-queue`, `dataprotection-keys`. Docker prefixes them with the
compose project name, which is the directory name (`code-coverage` for `/var/www/code-coverage`), so
the internal network is `code-coverage_coverage-internal`. The backup commands in the README rely on
that name.

Traefik labels on `coverage-app`:

| Label | Value | Why |
|---|---|---|
| `traefik.enable` | `true` | |
| `traefik.http.routers.coverage.rule` | ``Host(`coverage.mintplayer.com`)`` | ⚠️ **Hard-coded, not interpolated.** Interpolating it from `.env` once produced an empty or CRLF-poisoned value, so SNI never matched and Traefik served its default self-signed certificate. A self-hosted instance edits this line. |
| `traefik.http.routers.coverage.entrypoints` | `websecure` | Traefik must have an entrypoint of exactly this name. |
| `traefik.http.routers.coverage.tls.certresolver` | `letsencrypt` | Traefik must have a certificate resolver of exactly this name. |
| `traefik.http.services.coverage.loadbalancer.server.port` | `8080` | Pinned so Traefik never guesses. The image exposes 8080 only, deliberately (Dockerfile). |
| `traefik.docker.network` | `web` | ⚠️ The app is on two networks; without this Traefik may pick the `coverage-internal` address, which it cannot reach — random 504s. |

## 2. Server `.env`

Source: [`apps/CodeCoverage/.env.example`](../../apps/CodeCoverage/.env.example) and the
`${…}` references in `docker-compose.yml`. The file lives next to `docker-compose.yml` on the server,
is **never written by a deploy**, and must have **LF line endings** — a CRLF file adds an invisible
`\r` to every value.

**Required** means the app does not start, or a core feature does not work, without it.

| Variable | Maps to | Default in compose | Required? | Meaning |
|---|---|---|---|---|
| `GITHUB_WEBHOOK_SECRET` | `GitHub__WebhookSecret` | *(none)* | Yes | The App's webhook secret. Every delivery's signature is validated against it. |
| `GITHUB_APP_ID` | `GitHub__Production__AppId` | *(none)* | Yes | The App's numeric id. Needed for installation tokens (check-runs, comments, `coverage.yml`, source view) and to recognise the App's own deliveries. `/health/ready` reports 503 without it. |
| `GITHUB_APP_CLIENT_ID` | `GitHub__Production__ClientId` | *(none)* | **Yes — startup throws without it** | OAuth client id for GitHub sign-in, the only sign-in provider. |
| `GITHUB_APP_CLIENT_SECRET` | `GitHub__Production__ClientSecret` | *(none)* | Yes | OAuth client secret. |
| `GITHUB_APP_SLUG` | `GitHub__Production__AppSlug` | empty | No | The App's public slug (`github.com/apps/<slug>`) for "install the App" links. Empty falls back to the generic apps page. |
| `GITHUB_DEVELOPMENT_APP_ID` | `GitHub__Development__AppId` | empty | No | A *second*, development App whose webhooks this production instance should **not** process but forward to connected developer clients over a websocket, so both Apps can share one webhook URL. Ignored in Development. |
| `COVERAGE_BADGE_SIGNING_KEY` | `Coverage__BadgeSigningKey` | empty | No | Signs the per-pull-request badge URL in the bot's PR comment, so a private repository's comment never carries the repo-wide badge token. Any high-entropy string (`openssl rand -hex 32`). Unset degrades those comments to text-only numbers; rotating it breaks only the badge images inside already-posted comments. |
| `EXTERNAL_LOGIN_LINKING` | `Spark__Auth__ExternalLoginLinking` | `Disabled` | No | `Disabled` \| `WhenSignedIn` \| `ConfirmByEmail`. ⚠️ `ConfirmByEmail` without mail configured **refuses startup**. |
| `MAIL_FROM_ADDRESS` | `Spark__Mail__From__Address` | empty | No | The switch for outgoing mail: empty means the app registers no mail transport at all, which is supported. |
| `MAIL_FROM_NAME` | `Spark__Mail__From__Name` | `MintPlayer Coverage` | No | Display name of the sender. |
| `MAIL_HOST` | `Spark__Mail__Smtp__Host` | `coverage-smtp` | No | Where the app hands mail. The default is the relay container; change it only to bypass the relay. |
| `MAIL_PORT` | `Spark__Mail__Smtp__Port` | `587` | No | Port on `MAIL_HOST`. |
| `MAIL_SENDER_DOMAIN` | relay `ALLOWED_SENDER_DOMAINS` | `coverage.mintplayer.com` | If mail is on and your domain differs | The only envelope-sender domain the relay accepts; anything else is refused rather than relayed. |
| `MAIL_HELO_HOSTNAME` | relay `HOSTNAME` | `coverage.mintplayer.com` | If mail is on and your domain differs | Announced in HELO and used in Message-ID. ⚠️ Must match the sending address's PTR. |
| `MAIL_DKIM_SELECTOR` | relay `DKIM_SELECTOR` | `mail` | No | The selector half of the `<selector>._domainkey.<domain>` TXT record. |
| `MAIL_RELAY_HOST` | relay `RELAYHOST` | empty | No | Empty: deliver directly to each recipient's MX (needs outbound 25). Set to a provider's submission host (e.g. `[smtp.example.com]:587`) to relay instead. |
| `MAIL_RELAY_USERNAME` | relay `RELAYHOST_USERNAME` | empty | With `MAIL_RELAY_HOST` | Provider credentials. |
| `MAIL_RELAY_PASSWORD` | relay `RELAYHOST_PASSWORD` | empty | With `MAIL_RELAY_HOST` | Provider credentials. |

Not in `.env` on purpose:

- **The public hostname.** Hard-coded in `docker-compose.yml` in three places: the Traefik `Host`
  rule, `Coverage__BaseUrl` and `Spark__Auth__PublicBaseUrl`. They must agree.
- **The RavenDB licence.** RavenDB ignores it as a setting; see the README's *RavenDB licence*.

Set directly in `docker-compose.yml` (not overridable from `.env`):

| Variable | Value | Meaning |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | ⚠️ Load-bearing: `Program.cs` builds the `GitHub:{environment}:*` key prefix from it, so any other value silently reads different keys and disables sign-in. |
| `Spark__RavenDb__Urls__0` | `http://coverage-raven:8080` | |
| `Spark__RavenDb__EnsureDatabaseCreated` | `true` | Creates the database (named by `Spark:RavenDb:Database`, `Coverage` from appsettings) on first start. |
| `Spark__DataProtection__KeysPath` | `/var/lib/codecoverage/dataprotection-keys` | On the `dataprotection-keys` volume. |
| `Coverage__BaseUrl` | `https://coverage.mintplayer.com` | Public base URL. Also the **OIDC audience** and the passkey relying-party id. |
| `Spark__Auth__PublicBaseUrl` | `https://coverage.mintplayer.com` | Base for links in mail; never taken from the request `Host`. |
| `GitHub__Production__PrivateKeyPath` | `/run/secrets/github-app.pem` | The bind-mounted App key. |
| `Spark__Mail__Smtp__Security` | `None` | The relay is internal and advertises STARTTLS it has no certificate for; `None` never sends STARTTLS. |
| relay `POSTFIX_inet_protocols` | `ipv4` | ⚠️ Mail leaves over IPv4 only; see the README's *Outgoing mail*. |
| relay `POSTFIX_smtp_address_preference` | `ipv4` | Kept alongside; on its own it only *prefers* IPv4. |
| Raven `RAVEN_Setup_Mode` | `None` | |
| Raven `RAVEN_Security_UnsecuredAccessAllowed` | `PublicNetwork` | Needed for unsecured access on a `0.0.0.0` bind. The topology, not this flag, keeps Raven private. |
| Raven `RAVEN_ServerUrl` | `http://0.0.0.0:8080` | |

## 3. Files next to the compose file

All server-managed: a deploy re-downloads `docker-compose.yml` and touches nothing else.

| Path (relative to the compose directory) | Required? | Owner / mode | Notes |
|---|---|---|---|
| `.env` | **Yes** — the deploy fails without it | readable by the deploying user | §2. LF line endings. |
| `github-app.pem` | **Yes** — the deploy fails without it | readable by UID **1654** (the image's `app` user), e.g. `chmod 644` | The **production** App's private key. ⚠️ If missing at first `up`, Docker creates a *directory* here. ⚠️ A single-file bind mount binds the inode: replace with `cat new.pem > github-app.pem`, never `mv`/`scp` over it. |
| `raven-license.json` | No | any; read whole, so pretty-printing and CRLF are fine | Without it the one-shot exits non-zero and Raven runs unlicensed. Also a single-file bind mount; same inode rule. `apps/*/raven-license.json` is gitignored. |
| `mail-dkim/<domain>.private` | Only with mail | owned **101:104** (opendkim in `boky/postfix:v4.3.0`), `600` | ⚠️ Must be *flat*, not `opendkim-genkey`'s `<domain>/<selector>.private`, or the relay delivers unsigned. |

## 4. Application configuration keys

Keys the CodeCoverage assemblies read themselves. Environment-variable form replaces `:` with `__`.
`{Env}` is the ASP.NET Core environment name (`Development` locally, `Production` in compose).

| Key | Default (source) | Required? | Read by | Meaning |
|---|---|---|---|---|
| `Coverage:BaseUrl` | `https://localhost:5200` (`appsettings.json`; same fallback in `Program.cs`) | Yes in production | `Program.cs` (OIDC audience, passkey server domain), `BrowseController`, `UploadsController`, feedback recipients | The public base URL, **without a trailing slash**. ⚠️ Changing it after users enrolled passkeys makes every passkey unusable, and every workflow's OIDC audience must change with it. |
| `Coverage:BadgeSigningKey` | unset (`Badges/BadgePrSignature.cs`) | No | PR-comment badges | See `COVERAGE_BADGE_SIGNING_KEY`. |
| `Coverage:Retention:ReportAttachmentDays` | `7` (`appsettings.json`; `DefaultRetentionDays = 7` in `Ingestion/ReapReportAttachmentsCronJob.cs`) | No | the reaper cron job | Days an uploaded raw report attachment is kept after parsing. Not mapped in compose. |
| `GitHub:WebhookSecret` | `""` (`appsettings.json`) | Yes | `Program.cs` → Spark GitHub webhooks | Webhook signature secret. |
| `GitHub:SmeeChannelUrl` | `""` (`appsettings.json`) | Development only | `Program.cs` | A smee.io channel relayed into the local webhook processor. |
| `GitHub:{Env}:ClientId` | unset | **Yes — `Program.cs` throws a named error** (except for `--spark-*` build commands) | sign-in, user-token refresh | OAuth client id. |
| `GitHub:{Env}:ClientSecret` | unset | Yes | sign-in, user-token refresh | OAuth client secret. |
| `GitHub:{Env}:AppId` | unset | Yes | webhooks (`ProductionAppId`), readiness probe, project automation's self-authored-event guard | The App this instance processes webhooks for. Locally that is the development App. |
| `GitHub:{Env}:PrivateKeyPath` | unset | Yes | installation tokens, readiness probe | Path to the App's `.pem`. |
| `GitHub:{Env}:AppSlug` | `coveragedevelopment` for `Development` (`appsettings.json`); unset otherwise | No | `Services/MyAccountsService.cs` | Install-link slug. |
| `GitHub:Development:AppId` *(outside Development)* | unset | No | `Program.cs` → `DevelopmentAppId` | Forward this App's webhooks to connected developer clients instead of processing them. ⚠️ Never set it on a developer machine: the processor then skips every recipient. |
| `AllowedHosts` | `*` (`appsettings.json`) | — | ASP.NET Core | Left open; Traefik's `Host` rule is the filter. Also why `Spark:ForwardedHeaders:ForwardHost` must stay off (§5). |

## 5. Spark framework keys

Framework options the app inherits. Only the keys that matter to hosting are listed; everything else
takes the framework default. Sources are under `libs/`.

**`Spark:RavenDb`** — `libs/spark/MintPlayer.Spark/Configuration/SparkOptions.cs`

| Key | Default | App value | Meaning |
|---|---|---|---|
| `Urls` | `[]` | `http://localhost:8080` (appsettings); `http://coverage-raven:8080` (compose) | RavenDB URLs. |
| `Database` | `Spark` | `Coverage` (appsettings) | ⚠️ The production database is still named `Coverage`; that is data, not a naming leftover. |
| `EnsureDatabaseCreated` | `false` | `true` (compose) | Create the database if absent. |
| `MaxConnectionRetries` / `RetryDelaySeconds` | `30` / `2` | — | Startup connection retries. |

**`Spark:DataProtection`** — `libs/spark/MintPlayer.Spark/Configuration/SparkDataProtectionOptions.cs`

| Key | Default | App value | Meaning |
|---|---|---|---|
| `ApplicationName` | entry assembly name | `CodeCoverage` (appsettings) | ⚠️ Changing it invalidates every cookie issued before. |
| `KeysPath` | unset | `/var/lib/codecoverage/dataprotection-keys` (compose) | Key ring as XML files. Only as durable as the volume behind it. |
| `Storage` | unset | unset | `RavenDb` keeps the ring as `DataProtectionKeys/` documents instead. ⚠️ Setting **both** `KeysPath` and `Storage` refuses startup; so does **neither** outside Development. |

**`Spark:ForwardedHeaders`** — `libs/spark/MintPlayer.Spark/Configuration/SparkForwardedHeadersOptions.cs`.
Not set by the app: the defaults fit a container reachable only through Traefik on a Docker bridge.

| Key | Default | Meaning |
|---|---|---|
| `KnownNetworks` | loopback + `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `fc00::/7` | Trusted proxy networks. Setting this or `KnownProxies` *replaces* the defaults. |
| `KnownProxies` | — | Trusted proxy addresses. ⚠️ Set it if the app is ever exposed directly or behind a proxy that is not on a private network. |
| `ProxyHops` | `1` | Only the rightmost `X-Forwarded-For` entry (the one Traefik appended) is read. |
| `ForwardHost` | `false` | ⚠️ Refused at startup while `AllowedHosts` is `*`. Traefik passes the original `Host` through, so it is not needed. |

Every rate limiter partitions on the resulting client address; Spark refuses to start outside
Development if the trust list is emptied.

**`Spark:Auth`** — `libs/authorization/MintPlayer.Spark.Authorization/`

| Key | Default | App value | Meaning |
|---|---|---|---|
| `ExternalLoginLinking` | `Disabled` | `${EXTERNAL_LOGIN_LINKING:-Disabled}` | Read explicitly by `Program.cs`. |
| `PublicBaseUrl` | unset | `https://coverage.mintplayer.com` (compose) | Base for links in auth mail (`Identity/SparkAuthLinkBuilder.cs`) and mail bounces. |

Set in code, not configuration (`Program.cs`): local credentials `Disabled` (the Spark default), passkeys `Enabled`,
`PasskeyServerDomain` = host of `Coverage:BaseUrl`.

**`Spark:Mail`** — `libs/mail/MintPlayer.Spark.MailManager/SparkMailOptions.cs`. MailManager is
registered only when both `Smtp:Host` and `From:Address` are non-empty (`Program.cs`).

| Key | Default | App value | Meaning |
|---|---|---|---|
| `ApplicationName` | unset | `Coverage` (appsettings) | |
| `From:Address` / `From:Name` | unset | `MAIL_FROM_ADDRESS` / `MintPlayer Coverage` | Sender. |
| `Smtp:Host` / `Smtp:Port` | unset / `587` | `coverage-smtp` / `587` (compose) | The relay. |
| `Smtp:Security` | `Auto` | `None` (appsettings and compose) | Never STARTTLS to the internal relay. |
| `Smtp:UserName` / `Smtp:Password` | unset | not mapped | The relay needs none; provider credentials belong on the relay (`MAIL_RELAY_*`). |
| `Smtp:Timeout` | 100 s | — | |
| `DeliveryRetentionDays` | `30` | — | |
| `Bounces:*` | disabled | not mapped | Bounce processing is not enabled for this app; see [`docs/guide-outgoing-mail.md`](../guide-outgoing-mail.md) §8. |

**`Spark:Messaging`** — `libs/messaging/MintPlayer.Spark.Messaging/SparkMessagingOptions.cs` and
`SparkQueueOptions.cs`. Nothing is configured by the app; the defaults are what production runs.

| Key | Default | Meaning |
|---|---|---|
| `SubscriptionMode` | `SingleSubscription` | ⚠️ One RavenDB data subscription for every queue. Keep it: the Community licence allows 3 subscriptions per database, and per-queue subscriptions beyond that failed silently in production (`CodeCoverage.Library/Feedback/CoverageQueues.cs`). |
| `MaxAttempts` | `5` | Before a message is dead-lettered. |
| `ClaimTtl` | 5 min | A killed handler's message is reclaimed after this. Keep `stop_grace_period` (120 s) below it. |
| `HandlerTimeout` | 10 min | A hung handler is cancelled after this. |
| `RetentionDays` | `7` | |
| `FallbackPollInterval` | 30 s | |
| `Queues:{name}:*` | none | Per-queue throttling/concurrency. The app's queues are `coverage-parse-session` and `coverage-publish-feedback` (`CoverageQueues.cs`), plus `coverage-project-automation` (`Feedback/ProjectAutomationQueue.cs`) and `spark-forge-all` (`CodeCoverage.Library/Forge/ForgeWebhookMessage.cs`). |

The expiration sweep the messaging host enables is pinned to 36 h in code
(`libs/messaging/MintPlayer.Spark.Messaging/SparkMessagingExtensions.cs`). ⚠️ That is a **Community
licence minimum**: a smaller value is rejected by the cluster and kills the RavenDB process.

**`Spark:RateLimiter`** — `libs/spark/MintPlayer.Spark/Extensions/SparkRateLimiterOptions.cs`

| Key | Default | App value | Meaning |
|---|---|---|---|
| `PermitLimit` / `Window` | `150` / 10 s | — | Per client IP. |
| `PathPrefixes` | `/spark`, `/connect` | `/spark`, `/connect`, `/api/browse` (set in `Program.cs`) | |

**`Spark:TimeZone`** — `libs/spark/MintPlayer.Spark/Configuration/SparkTimeZoneOptions.cs`

| Key | Default | Meaning |
|---|---|---|
| `CookieName` | `spark-timezone` | Cookie read when the `X-Spark-Timezone` header is absent. Empty disables the cookie. Not set by the app. |

## 6. GitHub App

Derived from the code that handles webhooks and calls the API; the README's
[§5](../../apps/CodeCoverage/README.md#5-the-github-app) explains each one. Create one App per
environment.

**URLs**

| Setting | Value | Source |
|---|---|---|
| Webhook URL | `https://<your-domain>/api/github/webhooks` | `GitHubWebhooksOptions.WebhookPath` default |
| Callback URL | `https://<your-domain>/signin-github` (locally `https://localhost:5200/signin-github`) | `GitHubAuthenticationExtensions.cs` pins `CallbackPath` |
| Setup URL | optional: the app's home page | |
| *Request user authorization (OAuth) during installation* | **unchecked** | `Program.cs` `OnRemoteFailure` |
| Dev-client websocket | `/spark/github/dev-ws` on the production instance (only with `GITHUB_DEVELOPMENT_APP_ID`) | `GitHubWebhooksOptions.DevWebSocketPath` |

**Permissions**

| Scope | Permission | Level | Needed for |
|---|---|---|---|
| Repository | Metadata | Read-only | Mandatory on every App. |
| Repository | Contents | **Read & write** | Source view and `coverage.yml` at a commit (read); `push` events; deleting a merged PR's head branch when a board opts in (write — `GitHubForgeClient.DeleteBranchAsync`). Read-only suffices without branch deletion. |
| Repository | Pull requests | **Read & write** | `pull_request` events (read); the sticky PR comment (write). |
| Repository | Checks | **Read & write** | The `coverage/project` and `coverage/patch` check-runs; `check_run` events. |
| Repository | Issues | Read-only | `issues` and `issue_comment` events — board automation only. |
| Organization | Projects | Read & write | Moving cards on Projects V2 boards (GraphQL, `GitHubProjectCards.cs`) — board automation only. |
| Organization | Members | Read-only | Required by GitHub to subscribe to the `organization` event (organization renames). |
| Account | Email addresses | Read-only | First sign-in reads `GET /user/emails` for a verified primary address; without it sign-in fails with `email_not_verified`. |

The Issues and Projects rows are derived from the project-automation code and
[`docs/coverage_project_automation_PRD.md`](../coverage_project_automation_PRD.md); GitHub's UI is the
only place an App's permissions are recorded, so verify them there. A raised permission only takes
effect once **each installation accepts it**.

**Webhook events to subscribe**

| Event | Handled by | Needed for |
|---|---|---|
| Repository | `GitHubEventsRecipient` | renames, transfers, deletion |
| Push | `GitHubEventsRecipient` | branch tracking |
| Pull request | `GitHubEventsRecipient`, project automation | authoritative PR ↔ branch labels, merged-PR cleanup, board rules |
| Organization | `GitHubEventsRecipient` | organization renames (action `renamed`) |
| Issues, Issue comment, Pull request review, Check run | `ProjectAutomationRouter` | board automation only |
| `installation` (incl. action `new_permissions_accepted`), `installation_repositories` | `GitHubEventsRecipient` | always delivered to Apps; no subscription needed |

⚠️ `installation_target` is also handled but **not relied on**: it is not among the subscribed events,
and an App receives only what it subscribes to — which is why `organization` is subscribed. The
comment in `GitHubEventsRecipient.cs` records the subscriptions measured on 2026-09-05 as `member`,
`membership`, `organization`, `pull_request`, `push`, `repository`, `team`, `team_add`; the
automation events were added afterwards. Subscribing to `organization` needs the **Organization →
Members: Read-only** permission on GitHub's side, even though no code calls a members API. (GitHub
requirement, not visible in this repository: confirm on the App's settings page.)

## 7. Deploy workflow

Source: [`.github/workflows/code-coverage-deploy.yml`](../../.github/workflows/code-coverage-deploy.yml).

**Triggers**: push to `master` touching any of the paths below, or `workflow_dispatch`. The deploy
job runs only on `refs/heads/master`. Concurrency cancels an in-progress run on the same ref.

**Path filter**: `apps/CodeCoverage/**` *except* `apps/CodeCoverage/action/**`; the Spark libraries
in the app's `ProjectReference` closure (`libs/spark/MintPlayer.Spark`, `…Abstractions`,
`libs/attributes/MintPlayer.Spark.Attributes`, `libs/authorization/…`, `libs/controllers/…`,
`libs/cron/…`, `libs/messaging/…` + `.Abstractions`, `libs/mail/…` + `.Abstractions`,
`libs/migrations/…`, `libs/webhooks/…GitHub` + `.DevTunnel`,
`libs/subscription_worker/…Abstractions`, `libs/source_generators/…SourceGenerators` +
`…LibraryGenerators`, `libs/socket_extensions/…`); `libs/node_packages/ng-spark/**` (auth entries included);
`package.json`, `package-lock.json`, `nx.json`, `tsconfig.base.json`. ⚠️
Hand-maintained: a new Spark dependency of CodeCoverage goes in **three** places, or it breaks
silently:
1. this deploy filter, or its changes will not deploy;
2. the same filter in `code-coverage-image-check.yml` (below), or a pull request that breaks the
   image through it is not checked;
3. the csproj `COPY` list before `dotnet restore` in `apps/CodeCoverage/CodeCoverage/Dockerfile`,
   which must be the full transitive `ProjectReference` closure, or the restore fails inside the
   image.

**Secrets, by name**

| Secret | Required? | Used for |
|---|---|---|
| `VPS_HOST` | Yes | SSH target. |
| `VPS_USERNAME` | Yes | SSH user. |
| `VPS_SSH_KEY` | Yes | Private half of a dedicated deploy key (ed25519) in that user's `authorized_keys`. |
| `VPS_PORT` | No | SSH port. |
| `VPS_SSH_KEY_PASSPHRASE` | No | If the deploy key has one. |
| `NX_CACHE_SERVER`, `NX_CACHE_RW_TOKEN`, `NX_CACHE_RO_TOKEN` | No | Exported as workflow env for the Nx remote cache; the Docker build deliberately does not use them (`--skip-nx-cache`). |
| `GITHUB_TOKEN` | automatic | Pushing the image, and — passed to the server — downloading `docker-compose.yml` and `docker login ghcr.io` there. |

**Job `build-and-push`** (permissions `contents: read`, `id-token: write`, `packages: write`,
`attestations: write`): builds `apps/CodeCoverage/CodeCoverage/Dockerfile` with Buildx and GHA layer
cache, pushes `ghcr.io/mintplayer/codecoverage` with `docker/metadata-action` tags (a push to
`master` produces `:master`), attests build provenance, and best-effort PATCHes the package to
public. It runs no tests and no model verification — `pull-request.yml` gates those before merge,
and a model mismatch refuses to start in Production anyway.

**Job `deploy`** (permissions `contents: read`, `packages: read`; timeout 25 min), over SSH, with `set -e`:

1. `mkdir -p /var/www/code-coverage && cd` there.
2. Fail if `.env` or `github-app.pem` is missing.
3. Replace `docker-compose.yml` with the one on `master`, from `raw.githubusercontent.com`.
4. `docker login ghcr.io` with the job's `GITHUB_TOKEN`.
5. `docker compose --env-file .env pull`, then `down --remove-orphans`, then `up -d --remove-orphans`.
   There is a short outage between `down` and `up`.
6. `docker image prune -f`, `docker compose ps`.
7. Poll `GET /health/ready` from inside `coverage-app` every 10 s, up to 66 times: 200 passes, 503
   fails the deploy immediately (the App key is unusable), anything else keeps polling; 660 s
   without a 200 fails the deploy. The bound is `coverage-app`'s 600 s healthcheck `start_period`
   plus a minute, so a slow startup migration does not fail a deploy that would have succeeded;
   keep the two in step.

The directory `/var/www/code-coverage` is hard-coded in the script.

**Pull-request image check.** [`.github/workflows/code-coverage-image-check.yml`](../../.github/workflows/code-coverage-image-check.yml)
builds the same Dockerfile, with the same context, on every pull request whose changes match the
deploy's path filter (mirrored, plus the two workflow files). Before it existed a broken Dockerfile
(a missing `COPY` for a new `ProjectReference`, a stage that no longer builds) surfaced first as a
failed production deploy. It has `contents: read` only, never logs in to a registry, never pushes
(`push: false`) and never deploys. It reads the GHA layer cache the master deploy writes
(`cache-from: type=gha`) but never writes to it, so nothing a pull request builds can be replayed
into a production image. Timeout 30 min; concurrency cancels a superseded run on the same branch.

## 8. The upload action

Source: [`apps/CodeCoverage/action/action.yml`](../../apps/CodeCoverage/action/action.yml); usage in
[`action/README.md`](../../apps/CodeCoverage/action/README.md).

| Input | Default | Meaning |
|---|---|---|
| `url` | *(required)* | The server's base URL. Trailing slashes are stripped (`src/main.ts`). |
| `token` | — | An upload token (`covt_…`). Optional when OIDC is available. |
| `use-oidc` | `false` | Authenticate with GitHub Actions OIDC; also used automatically when no token is given and OIDC is available. Needs `permissions: id-token: write`; unavailable to fork PRs. |
| `files` | auto-detect | Explicit report files or globs. |
| `directory` | workspace | Root for auto-detection. |
| `flags` | — | Comma-separated labels for this upload. |
| `partial` | `false` | This upload measures a subset (e.g. `nx affected`). |
| `base-sha` | — | The base the affected computation ran against. |
| `carry-forward` | `true` | Allow unchanged files to be carried from the base. |
| `name` | job name | Session name. |
| `finish` | `false` | Finalize the build after this upload. |
| `fail-ci-if-error` | `false` | Fail the step on upload failure. |
| `disable-search` | `false` | Upload only the listed files. |
| `wait-for-finalize` | `false` | Wait for the result and expose it as outputs. |
| `wait-timeout` | `1800` | Seconds; matches the server's 30-minute ceiling. |
| `wait-poll-interval` | `5` | Seconds between polls. |

**OIDC audience**: the action requests its id-token with `audience = url` (slash-stripped;
`src/credential.ts`), and the server validates `aud` against `Coverage:BaseUrl`
(`Program.cs`, `ValidAudience`). ⚠️ The two must be the **same string**. A self-hosted instance sets
`Coverage:BaseUrl` without a trailing slash, and its workflows pass exactly that as `url`.

## 9. Fixed in code

Not configurable, but worth knowing when sizing a proxy or a CI fleet (`Program.cs`):

| Rate-limit policy | Limit | Partitioned by |
|---|---|---|
| Spark global (`/spark`, `/connect`, `/api/browse`) | 150 / 10 s | client IP |
| `browse` | 300 / min | client IP |
| `uploads` | 60 / min | upload token hash, else client IP |
| `uploads-status` | 300 / min | upload token hash, else client IP |
| `fork-uploads` | 10 / min | client IP + target repository |
| `badges` | 600 / min | client IP |

Health endpoints: `GET /health` (process answers; used by the compose healthcheck) and
`GET /health/ready` (round-trips an App JWT to GitHub; 503 while the key is unusable; used by the
deploy).

## 10. Inconsistencies found

Found while writing this reference (2026-09-29), and fixed in the same pull request:

- **The relay's healthcheck always failed.** It ran `sh -c 'exec 3<>/dev/tcp/…'`, but `/dev/tcp` is
  a bash feature and `boky/postfix`'s `sh` is dash, so production reported a working
  `coverage-smtp` as `unhealthy` for days. It now runs under `bash`, like the other two.
- **Deploy readiness budget versus start period.** The deploy polled `/health/ready` for 180 s, and
  its comment said that "matches the compose healthcheck's start_period (60s)", but `coverage-app`'s
  `start_period` is 600 s, raised for a startup migration measured at roughly 100 s on a restored
  copy. A longer migration would have failed the deploy while the container carried on and became
  healthy. The bound is now 660 s (still a poll that exits on the first 200) and the job timeout
  25 min.
- **Deploy path-filter comment** named `coverage-action.yml`; the workflow is
  `coverage-action-publish.yml`.
- **Dockerfile restore closure.** Its comment says the csproj `COPY` list must be the app's full
  transitive `ProjectReference` closure, but it omitted `libs/attributes/MintPlayer.Spark.Attributes`
  (referenced by `MintPlayer.Spark.Abstractions`) and `libs/source_generators/MintPlayer.Spark.LibraryGenerators`
  (an analyzer reference of `CodeCoverage` and `CodeCoverage.Library`). It now lists exactly the
  computed closure, 22 projects. The deploy path filter already covered both.
- **A redundant `dotnet build` in the Dockerfile.** The template's `dotnet build -o /app/build` wrote
  output nothing ever copied, and `dotnet publish` then compiled the whole closure again. It is
  removed; the `publish` stage sets its own `WORKDIR`. Measured locally: the .NET steps went from
  58.8 s to 40.8 s, and the image holds the same 95 files in `/app`.
- **No image build before merge.** The Dockerfile was built only by the post-merge deploy; the new
  `code-coverage-image-check.yml` builds it on pull requests (§7).
- **`.env.example`** pointed to "README 'Deployment' step 2" for the key fingerprint check; it now
  names [§3 step 4](../../apps/CodeCoverage/README.md#3-the-server-docker-and-traefik).
- **Stale RavenDB-licence history.** The README used to say running unlicensed "is what this
  deployment did for its whole life"; production has since been measured as a registered Community
  licence (`CoverageQueues.cs`, and `SparkMessagingExtensions.cs` on 2026-09-07). The README now
  says only that unlicensed is supported.
- **Earlier README drift, corrected in the docs:** it asked for a .NET 10 SDK (the Dockerfile uses
  `sdk:11.0` / `aspnet:11.0`), used `Coverage` as the project path in its `dotnet` commands, said the
  deploy workflow runs tests (it does not; `pull-request.yml` does), said no organization
  permissions are needed (board automation and the `organization` event need them), and listed
  Contents as read-only although branch deletion needs write.

Still open, because they live on the server rather than in the repository:

- **No scheduled database backup.** Nothing on the host backs up `code-coverage_raven-data`; see the
  README's *Automating it*.
- **A leftover `TRAEFIK_HOST` in the server's `.env`.** Nothing reads it since the hostname was
  hard-coded into `docker-compose.yml`; it can be deleted.
