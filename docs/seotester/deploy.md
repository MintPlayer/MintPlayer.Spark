# SeoTester: deploy runbook

How **https://seotester.mintplayer.com** runs on the VPS it shares with coverage.mintplayer.com, what the owner sets up
once, which credentials to register, and what to measure after the first deploy. Spec: [seo_analyzer_PRD.md](../seo_analyzer_PRD.md)
§1 "Hosting constraints", D3, D6, M12. The app itself: [README.md](README.md). CodeCoverage's hosting, which this
mirrors and shares: [docs/code-coverage/hosting.md](../code-coverage/hosting.md) and `apps/CodeCoverage/README.md`.

Status (2026-10-11): **live.** PR #505 merged, CodeCoverage's RavenDB secured (raven-tls-migration.md §4), SeoTester's
first deploy green, sign-in with Microsoft, GitHub and Google working, mail delivered (SPF + DKIM). §0 records what was
set up and where; the numbered sections are the procedure.

## 0. Where everything is managed (as set up 2026-10-11)

Names, IDs and places only. **No secret belongs in this repository**: client secrets, the pfx password, `.env` values,
`ca.key`, the DKIM and operator keys stay on the VPS (or with the owner). An OAuth client ID is not a secret: it is in
every sign-in URL.

| What | Where | Identity | Notes |
|---|---|---|---|
| **Microsoft** sign-in | [entra.microsoft.com](https://entra.microsoft.com) → App registrations → *SeoTester* (the owner's default directory) | client ID `d3e309f8-bd12-4e59-ad34-e1db9647ab22` | All Microsoft accounts (work/school and personal). Redirects: `https://seotester.mintplayer.com/signin-microsoft`, `https://localhost:5013/signin-microsoft`. ⚠️ The client secret **expires** (at most 24 months): renew it under *Certificates & secrets* before then and replace `MICROSOFT_CLIENT_SECRET`. |
| **GitHub** sign-in | OAuth App *SeoTester* in the **MintPlayer organization**: [settings](https://github.com/organizations/MintPlayer/settings/applications/3923314) | client ID `Ov23liowLf6rF9NW7LHd` | Owned by the org, not a person (OAuth Apps cannot be transferred: create them under the org). Redirects: production and `https://localhost:5013/signin-github`. |
| **Google** sign-in | Google Cloud project **`seotester-mintplayer`** → Google Auth Platform | OAuth client *SeoTester production* (Web application) | Audience External, **In production** (not Testing: there only listed test users may sign in). Branding: app name, home page, `/privacy`, `/terms`, authorized domain `mintplayer.com`; **no logo** (a logo requires Google's verification). Scopes `openid profile email` are non-sensitive: no review. Redirects: production and `https://localhost:5013/signin-google`. |
| **X** sign-in | **Not set up**: console.x.com, project *SeoTester* (empty) | — | X's API is pay-per-use and the account has no credits; the `users/me` call every X sign-in makes would be refused. `X_CLIENT_ID` stays empty, so no X button. To enable: buy credits, then an app in that project with OAuth 2.0 (Web App, *Request email*), callbacks `/signin-twitter` (production and localhost). X refuses new apps while a suspended one exists. |
| **DNS** of `mintplayer.com` | Qweb (formerly Foxxl): [mijn.qweb.nl](https://mijn.qweb.nl) → hosting → Plesk → *DNS Settings* | name servers `ns10.foxxl.{nl,com,net}` | `seotester` A/AAAA → the VPS; TXT `v=spf1 ip4:188.245.190.60 ip6:2a01:4f8:c0c:f87c:: -all`; TXT `mail._domainkey.seotester` = the DKIM public key (§3.2). ⚠️ Plesk keeps added records pending until **Update** in the banner is clicked. `mintplayer.com` has DMARC `sp=reject`: a subdomain without SPF/DKIM cannot send. |
| **Mail** | `coverage-smtp` (CodeCoverage's relay) | `MAIL_SENDER_DOMAIN=coverage.mintplayer.com seotester.mintplayer.com` in **CodeCoverage's** `.env` | DKIM key `/var/www/code-coverage/mail-dkim/seotester.mintplayer.com.private` (101:104, 0600). Without the domain in that list the relay answers `554 5.7.1 Recipient address rejected: Access denied` and the mail is dead-lettered. |
| **Database** | `SeoTester` on `coverage-raven` (TLS, client certificates) | certificate `seotester` (Admin on `SeoTester` only) | Renewed automatically, nightly check: `raven-cert-renew --status` on the VPS ([raven-tls-migration.md](../code-coverage/raven-tls-migration.md) §6). |
| **Secrets on the VPS** | `/var/www/seotester/.env` (0600) | — | Sign-in client IDs and secrets, `SEO_FETCHER_KEY`, `LEGAL_*`, optional VAPID and PageSpeed keys (§4). |

**Local development** uses the same three OAuth clients through their `localhost:5013` redirects. Put the IDs and secrets
in user secrets yourself, never through a tool that shows them:
`dotnet user-secrets set "Spark:Auth:Providers:Google:ClientId" "…"` (and `…:ClientSecret`; likewise `MicrosoftAccount`,
`GitHub`) in `apps/SeoTester/SeoTester`. The trade-off: production and development share a client.

**Linking a second provider** (e.g. Google to an account made with Microsoft): SeoTester runs
`ExternalLoginLinking = ConfirmByEmail` (`Program.cs`), so there is **no** "link while signed in" endpoint, and the
account page's link button answers 404 (a known client gap, left as is). The working path: sign out, sign in with the
new provider; when its address belongs to an existing account a confirmation mail is sent, and its link attaches the
provider.

**Lessons from the first deploy (2026-10-11):**
- The image must let its `app` user **start** Playwright's driver: `dotnet publish` writes it 0744 root, and every
  render failed "Permission denied", hanging the audit in its render stage. The Dockerfile `chmod`s it, and
  `seotester-image-check` starts it from a shell as `app` (not via `--entrypoint`, which passes even on a broken image).
- `seotester-deploy` and `code-coverage-deploy` run from the same merge; SeoTester's refuses until CodeCoverage's has
  put `coverage-raven` on `seo-raven` (§3.0). This time it went through in one pass.

## 1. What runs where

```
                      Internet
                         │ 443
                    ┌────┴────┐  network "web" (external, Traefik's)
                    │ Traefik │──────────────┐
                    └────┬────┘              │
                         │ coverage-app      │ seotester-app :8080   (/spark/live: WebSocket, upgraded by default)
  ┌──────────────────────┴──────────┐  ┌─────┴──────────────────────────────┐
  │ /var/www/code-coverage          │  │ /var/www/seotester                 │
  │ project "code-coverage"         │  │ project "seotester"                │
  │  coverage-raven  (DB Coverage,  │◄─┤  seotester-app ──┐                 │
  │                   DB SeoTester) │  │                  │ seo-browser     │
  │  coverage-smtp   (relay)        │◄─┤                  │ (internal: no   │
  │  coverage-app                   │  │  seo-chromium ◄──┘  egress at all) │
  │  (coverage-db, coverage-mail:   │  │                                    │
  │   CodeCoverage's own, internal) │  │                                    │
  └─────────────────────────────────┘  └────────────────────────────────────┘
     seotester-app meets coverage-raven on "seo-raven" and coverage-smtp on "seo-mail": external, internal: true,
     owned by no project, created by either deploy, removed by neither (PRD D55)

  seotester-app ──seo-fetch (internal)──► seo-fetcher ──seo-egress──► Internet (the audited sites, RDAP, PageSpeed)
     seo-fetcher has no database client and no other network: every request to a target leaves from it (PRD D54)
```

| Service | Image | Networks | Notes |
|---|---|---|---|
| `seotester-app` | `ghcr.io/mintplayer/seotester:master` (pull-only, built by `seotester-deploy.yml`) | `web`, `seo-raven`, `seo-mail`, `seo-browser`, `seo-fetch` | `mem_limit 1g`; `GET /health` healthcheck, start period 300 s; volume `dataprotection-keys`; Traefik labels for `seotester.mintplayer.com` |
| `seo-chromium` | `ghcr.io/mintplayer/seotester-chromium:1.63.0` (= Microsoft.Playwright's version) | `seo-browser` **only** | `shm_size 512m`, `mem_limit 1g`, `pids_limit 512`, `init`, no capabilities, no port published; TCP healthcheck on 3000 |
| `seo-fetcher` | `ghcr.io/mintplayer/seotester-fetcher:master` (built by the same deploy) | `seo-fetch`, `seo-egress` **only** | PRD D54: the SSRF-safe clients, TLS probes and DNS behind an internal API; `read_only` (tmpfs `/tmp`), no capabilities, `mem_limit 512m`, `pids_limit 256`, no port, no volume; `GET /health` healthcheck; key `SEO_FETCHER_KEY` |

- **Database:** CodeCoverage's RavenDB server (`coverage-raven`, Community licence), database **`SeoTester`**, created
  by the operator (§3.0). **Secured** (PRD D56): `https://coverage-raven:8080` on `seo-raven`, with SeoTester's own
  client certificate, registered with Admin on `SeoTester` and nothing else, and the private CA of the server's
  certificate (`Spark__RavenDb__CertificateFile` / `CertificateKeyFile` / `ServerCertificateAuthorityFile`, files in
  `/var/www/seotester/raven-certs/`). `EnsureDatabaseCreated` is `false`: creating a database is cluster admin.
- **Mail:** CodeCoverage's relay `coverage-smtp:587` (no credentials; `Security=None` as CodeCoverage). It must accept
  the sender domain `seotester.mintplayer.com` (§3.2).
- **Why the shared server and relay:** one RavenDB process and one mail reputation on a small VPS, both already
  operated (licence activation, DKIM, PTR). They meet on two networks, one per service (owner decision D55, which
  replaces D48's single `mintplayer-backend`): `seo-raven` (the app and RavenDB) and `seo-mail` (the app and the
  relay), both external, `internal: true`, declared by both compose files; neither stack's `up`/`down` touches them,
  and both deploys create them when missing. The cost: SeoTester needs CodeCoverage's database running (its deploy
  refuses until `coverage-raven` and `coverage-smtp` are on them), and CodeCoverage's maintenance windows are SeoTester's too.
- **The Chromium container** can reach nothing: `seo-browser` is `internal: true`, and Chromium itself never resolves a
  name or opens a connection (`--host-resolver-rules=MAP * ~NOTFOUND`, a dead proxy; SeoTester fulfils every request
  through its SSRF-safe client, PRD §6). It can reach `seotester-app:8080` on that network, which only matters if both
  layers fail.
- **The fetcher** (PRD D54) is the only container that talks to audited sites, the registries (RDAP, IANA,
  hstspreload) and PageSpeed Insights, and it serves Chromium's routed requests too. It has a route out
  (`seo-egress`, its own) and `seo-fetch` (internal, the app's API calls, authenticated with `SEO_FETCHER_KEY`), and no
  network to RavenDB, the relay or Chromium; its image has no database client. The app keeps politeness, robots.txt and
  the caches. If the fetcher is down, crawl stages fail and are retried by Messaging; the site itself stays up.
- **The app's own route out** is Traefik's `web`: it still needs it for the sign-in providers' token endpoints and Web
  Push. It sends no request to an audited site (in Production it has no client for one, and it refuses to start with
  `SeoTester:Fetcher:Mode=InProcess`). Narrowing that egress is PRD Q-D54a.

The files: `apps/SeoTester/docker-compose.yml`, `apps/SeoTester/.env.example`, `.github/workflows/seotester-deploy.yml`
(push to master or *Run workflow*), `.github/workflows/seotester-image-check.yml` (pull requests: builds the three images, no
push). `tools/verify-seotester-deploy.test.mjs` and `tools/verify-backend-networks.test.mjs` (`npm run test:tools`) check
them against each other and the code.

## 2. The deploy workflow

`seotester-deploy.yml`, on a push to `master` touching SeoTester or a library it references (the path list equals the
Dockerfile's COPY lines and the csproj graph; the tools test keeps the three equal), or *Run workflow*:

1. **build-and-push:** `apps/SeoTester/SeoTester/Dockerfile` → `ghcr.io/mintplayer/seotester:master` (+ the sha tag),
   provenance attestation, makes the package public (best effort).
2. **build-chromium:** `apps/SeoTester/chromium/Dockerfile` → `ghcr.io/mintplayer/seotester-chromium:<Playwright version>`
   (read from the Dockerfile's `FROM`).
3. **build-fetcher:** `apps/SeoTester/SeoTester.Fetcher/Dockerfile` → `ghcr.io/mintplayer/seotester-fetcher:master` (+ the
   sha tag), from the same commit as the app (PRD D54).
4. **deploy** (master only, after all three; also refuses without a `SEO_FETCHER_KEY` of ≥ 32 characters in `.env`): SSH with the **same repository secrets as CodeCoverage's deploy** (`VPS_HOST`,
   `VPS_USERNAME`, `VPS_SSH_KEY`, `VPS_PORT`, `VPS_SSH_KEY_PASSPHRASE`; `GITHUB_TOKEN` is automatic): no new secret.
   On the VPS: refuses without `/var/www/seotester/.env` or without one of `raven-certs/seotester.crt`, `.key`, `ca.crt`;
   creates `seo-raven` and `seo-mail` `--internal` if they are missing (the same block as CodeCoverage's deploy) and
   refuses one that is not internal; refuses, before touching the running stack, while `coverage-raven` is not on
   `seo-raven` or `coverage-smtp` not on `seo-mail` (CodeCoverage not yet deployed with the compose file that joins them, §3.0); downloads `docker-compose.yml` from master; `docker compose pull`, `down --remove-orphans`, `up -d`; prunes images;
   then waits for `GET /health` = 200 inside the container (36 × 10 s, the healthcheck's start period + 1 min).

Bumping Playwright: change `Microsoft.Playwright` in `SeoTester.csproj`, the chromium Dockerfile's `FROM` and
`playwright-core@`, and the tag in `docker-compose.yml`, together (the tools test fails otherwise).

## 3. First-time setup on the VPS (owner)

Run on the VPS as the deploy user unless noted. None of these print a secret.

### 3.0 The secured database, the shared networks, and the order with CodeCoverage (once)

SeoTester reaches `coverage-raven` over `seo-raven` and `coverage-smtp` over `seo-mail`, which CodeCoverage's compose
file joins (PRD D55), and RavenDB is secured with client certificates (D56). Both come with CodeCoverage's one-time
migration, [raven-tls-migration.md](../code-coverage/raven-tls-migration.md) §4, which also generates SeoTester's certificate, puts it in `/var/www/seotester/raven-certs/`,
registers it (Admin on `SeoTester` only) and creates the database `SeoTester`. In this order:

1. **CodeCoverage's migration**, [raven-tls-migration.md](../code-coverage/raven-tls-migration.md) §4.1–4.8 (it ends with the PR merged and CodeCoverage's deploy green). The same
   push may start `seotester-deploy.yml`: until CodeCoverage is on the new networks it **refuses** ("deploy CodeCoverage
   first") and changes nothing; re-run it (*Run workflow*) afterwards.
2. **Check** that the database and the relay are on their networks:

   ```bash
   docker network inspect seo-raven --format '{{.Internal}} {{range .Containers}}{{.Name}} {{end}}'   # true code-coverage-coverage-raven-1
   docker network inspect seo-mail  --format '{{.Internal}} {{range .Containers}}{{.Name}} {{end}}'   # true code-coverage-coverage-smtp-1
   ls -l /var/www/seotester/raven-certs/                                                              # ca.crt seotester.crt seotester.key
   ```
3. **Deploy SeoTester** (§3.6).

Either deploy can run first from then on: each creates the networks when they are missing, and neither `down` removes
them. By hand, the block both workflows run:
`for net in seo-raven seo-mail; do docker network inspect "$net" > /dev/null 2>&1 || docker network create --internal "$net"; done`.

### 3.1 DNS and TLS

The A and AAAA records for `seotester.mintplayer.com` exist. Check they point at the VPS:

```bash
dig +short seotester.mintplayer.com A      # expect 188.245.190.60
dig +short seotester.mintplayer.com AAAA   # expect the VPS's IPv6 address
```

TLS needs nothing by hand: Traefik's `letsencrypt` resolver requests the certificate on the first request that matches
the router `Host(\`seotester.mintplayer.com\`)` (the labels in the compose file). After the first deploy:

```bash
curl -sS -o /dev/null -w '%{http_code}\n' https://seotester.mintplayer.com/health          # 200
echo | openssl s_client -connect seotester.mintplayer.com:443 -servername seotester.mintplayer.com 2>/dev/null \
  | openssl x509 -noout -issuer -enddate                                                       # Let's Encrypt, ~90 days
```

A self-signed "TRAEFIK DEFAULT CERT" means the Host rule did not match or the ACME challenge failed:
`docker logs traefik 2>&1 | grep -i seotester`.

### 3.2 Mail: let the relay send for seotester.mintplayer.com

**Done 2026-10-11** (§0 "DNS" and "Mail"): records added in Plesk, `opendkim-testkey` "key OK", the relay recreated
with both domains, a test mail DKIM-signed for `d=seotester.mintplayer.com` and accepted by Outlook (`dsn=2.6.0`).
Until then every SeoTester mail was refused at the relay (`554 5.7.1 … Access denied`). Two adjustments to the steps
below, as run: `opendkim-genkey -D` needs its output folder to exist (`mkdir -p` it first), and the SPF record is the
same as CodeCoverage's, IPv4 and IPv6.

SeoTester **requires** mail (account-link confirmations, `ConfirmByEmail`; invitations; notifications). The relay accepts
only the domains in CodeCoverage's `MAIL_SENDER_DOMAIN` and signs only those it has a DKIM key for
(`apps/CodeCoverage/README.md` "Outgoing mail" has the traps; both apply here).

1. DNS for the sender domain (at the `mintplayer.com` DNS host). `mintplayer.com` publishes DMARC `sp=reject`, so a
   subdomain that fails alignment is **rejected**:
   - `seotester.mintplayer.com` TXT `v=spf1 ip4:188.245.190.60 -all` (IPv4 only, as CodeCoverage: the relay sends over IPv4).
   - `mail._domainkey.seotester.mintplayer.com` TXT: the record `opendkim-genkey` writes in step 2.
2. The DKIM key, flat and owned by opendkim (`101:104`):

   ```bash
   cd /var/www/code-coverage/mail-dkim
   docker run --rm -v "$PWD":/out alpine:3.20 sh -c \
     'apk add --no-cache opendkim-utils && mkdir -p /out/seotester.mintplayer.com && opendkim-genkey -b 2048 -d seotester.mintplayer.com -s mail -D /out/seotester.mintplayer.com'
   cat seotester.mintplayer.com/mail.txt        # the DNS record for step 1 (public)
   mv seotester.mintplayer.com/mail.private seotester.mintplayer.com.private
   sudo chown -R 101:104 . && sudo chmod 600 *.private
   ```
3. In `/var/www/code-coverage/.env`: `MAIL_SENDER_DOMAIN=coverage.mintplayer.com seotester.mintplayer.com` (a
   space-separated list; the default is CodeCoverage's domain alone). Then recreate only the relay:
   `cd /var/www/code-coverage && docker compose --env-file .env up -d coverage-smtp`.
4. Verify the key against DNS (`key OK`):

   ```bash
   docker run --rm -v /var/www/code-coverage/mail-dkim:/keys:ro alpine:3.20 sh -c \
     'apk add --no-cache opendkim-utils bind-tools >/dev/null &&
      opendkim-testkey -d seotester.mintplayer.com -s mail -k /keys/seotester.mintplayer.com.private -vvv'
   ```

After the first deploy, sign in once with a provider whose address is not yet confirmed (or link a second provider) and
check the mail arrives with `dkim=pass` and `dmarc=pass` in its headers.

### 3.3 The directory and `.env`

```bash
sudo mkdir -p /var/www/seotester && sudo chown "$USER" /var/www/seotester && cd /var/www/seotester
curl -sfO https://raw.githubusercontent.com/MintPlayer/MintPlayer.Spark/master/apps/SeoTester/.env.example
cp .env.example .env && chmod 600 .env
${EDITOR:-nano} .env      # fill in §4's values
```

Deploys never write `.env`; the workflow refuses to deploy without it, and without a `SEO_FETCHER_KEY` of at least 32
characters (PRD D54). Generate that key once, straight into the file, so it is never shown:

```bash
sed -i "s/^SEO_FETCHER_KEY=.*/SEO_FETCHER_KEY=$(openssl rand -hex 32)/" .env
```

Both `seotester-app` and `seo-fetcher` read it; changing it later needs both recreated (any deploy does).

### 3.4 The database and its expiration

The operator created `SeoTester` on `coverage-raven` (§3.0); the first start switches **document expiration** on
at the Community licence's floor, **every 36 h** (`DeleteFrequencyInSec` 129600; Spark's `Spark:Expiration`, which
refuses anything shorter). Page details (7 d), frontiers (2 d), cool-downs, notifications, invitations and the abuse log
(1 y) expire through it; every read hides expired documents, so the 36 h lag is invisible. Check after the first start:

```bash
C=/var/www/code-coverage   # the operator certificate lives with CodeCoverage (raven-tls-migration.md §3)
OP="docker run --rm --network code-coverage_coverage-db -v $C/raven-operator/operator.crt:/op.crt:ro -v $C/raven-operator/operator.key:/op.key:ro -v $C/raven-certs/ca.crt:/ca.crt:ro curlimages/curl:8.11.1 -sS --cert /op.crt --key /op.key --cacert /ca.crt"
$OP "https://coverage-raven:8080/databases?namesOnly=true"
$OP "https://coverage-raven:8080/admin/databases?name=SeoTester" | grep -o '"Expiration":{[^}]*}'
# expect: "Expiration":{"Disabled":false,"DeleteFrequencyInSec":129600,...}
```

The licence is the server's (CodeCoverage's `raven-license.json`; Community today): Changes API yes, at most **3
subscriptions per database**, revisions capped. SeoTester's Messaging uses one subscription.

**Backups:** none are scheduled for either database (CodeCoverage README "Backing up the database"). The same manual
export works for `SeoTester` (replace `Coverage` in the URL); a periodic backup task should cover both.

### 3.5 GitHub packages

After the first workflow run, check that `ghcr.io/mintplayer/seotester` and `ghcr.io/mintplayer/seotester-chromium` are
**public** (GitHub → MintPlayer → Packages → each → Package settings): the workflow's "make public" step is best effort.
The VPS pulls without credentials otherwise only after `docker login ghcr.io`, which the deploy does with the run's token.

### 3.6 The first deploy

GitHub → Actions → **seotester-deploy** → *Run workflow* on `master` (after this branch is merged). Watch the readiness
lines; on failure: `cd /var/www/seotester && docker compose --env-file .env logs --tail 200 seotester-app`.

Then make yourself the administrator (settings, catalogue, dashboard): sign in once, then add `Administrators` to your
user's `Roles`. The address goes in as a **query parameter**, never spliced into the RQL:

```bash
# $OP as in §3.4 (the operator certificate)
$OP -X PATCH \
  "https://coverage-raven:8080/databases/SeoTester/queries" -H "Content-Type: application/json" \
  --data '{"Query":{"Query":"from SparkUsers where NormalizedEmail = $email update { if (this.Roles.indexOf(\"Administrators\") < 0) this.Roles.push(\"Administrators\"); }","QueryParameters":{"email":"YOU@EXAMPLE.COM"}}}'
```

(`NormalizedEmail` is upper case.) Sign out and in again.

## 4. Credentials to register, and where each goes

All in `/var/www/seotester/.env` (chmod 600), read by the compose file into the container's environment. Never in the
repository, never in an `appsettings*.json`. The repository secrets of the workflow are CodeCoverage's (§2): nothing new.

### 4.1 Sign-in providers (PRD §4.1, D2, D13)

At least one is required (with none, the app refuses to start). A provider whose client id is empty is not offered.

| Provider | Register at | Redirect URI (exactly) | Scopes / settings | `.env` | Config key |
|---|---|---|---|---|---|
| Microsoft (Entra, personal **and** work/school) | entra.microsoft.com → App registrations → New; *Accounts in any organizational directory and personal Microsoft accounts*; platform **Web**; a client secret under *Certificates & secrets* | `https://seotester.mintplayer.com/signin-microsoft` | `openid`, `profile`, `email` (the preset asks); `common` endpoint | `MICROSOFT_CLIENT_ID`, `MICROSOFT_CLIENT_SECRET` | `Spark:Auth:Providers:MicrosoftAccount:ClientId` / `ClientSecret` |
| GitHub | github.com → Settings → Developer settings → **OAuth Apps** → New (homepage `https://seotester.mintplayer.com`) | `https://seotester.mintplayer.com/signin-github` | `user:email` | `GITHUB_CLIENT_ID`, `GITHUB_CLIENT_SECRET` | `Spark:Auth:Providers:GitHub:…` |
| X | developer.x.com → Projects & Apps → the app → *User authentication settings*: **OAuth 2.0**, type *Web App*, **Request email from users** on (privacy policy URL `https://seotester.mintplayer.com/privacy`, terms URL `https://seotester.mintplayer.com/terms`, §4.3) | `https://seotester.mintplayer.com/signin-twitter` | `users.read`, `tweet.read`, `users.email`; the **OAuth 2.0 Client ID and Client Secret**, not the 1.0a API key | `X_CLIENT_ID`, `X_CLIENT_SECRET` | `Spark:Auth:Providers:Twitter:…` |
| Google | console.cloud.google.com → APIs & Services → *OAuth consent screen* (External, app name SeoTester, scopes below), then Credentials → *OAuth client ID* → Web application | `https://seotester.mintplayer.com/signin-google` | `openid`, `profile`, `email` | `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | `Spark:Auth:Providers:Google:…` |

For local development, add `https://localhost:5013/signin-…` as a second redirect URI. ~~on a development app instead
of the production one~~ **As set up (owner, 2026-10-11): on the production apps themselves** (§0), so local
development shares their client; a separate development app remains the stricter option (README "Running it
locally"). Copy a client secret only when it is shown (Entra, GitHub and Google show it once), straight into `.env` or
user secrets, never into a chat, a tool or the repository.

### 4.2 Everything else

| What | How to get it | `.env` | Config key |
|---|---|---|---|
| **PageSpeed Insights / CrUX** | The key already in your local user secrets as `SeoTester:PageSpeed:ApiKey` (Google Cloud → APIs & Services → Credentials). Restrict it: *API restrictions* = PageSpeed Insights API; *Application restrictions* = IP addresses `188.245.190.60` (and the VPS's IPv6) | `PAGESPEED_API_KEY` | `SeoTester:PageSpeed:ApiKey` |
| **Web Push (VAPID)** | Generate once (below). `VAPID_SUBJECT` is `mailto:` your address. Changing the pair later drops every browser's subscription | `VAPID_PUBLIC_KEY`, `VAPID_PRIVATE_KEY`, `VAPID_SUBJECT` | `Spark:Notifications:WebPush:PublicKey` / `PrivateKey` / `Subject` |
| **SMTP** | Nothing to register: CodeCoverage's relay, once §3.2 is done. Overrides only if you relay elsewhere | `MAIL_HOST`, `MAIL_PORT`, `MAIL_FROM_ADDRESS`, `MAIL_FROM_NAME` (all optional) | `Spark:Mail:Smtp:Host` / `Port`, `Spark:Mail:From:Address` / `Name` |
| **RavenDB** | The client certificate and CA in `/var/www/seotester/raven-certs/` (§3.0, [raven-tls-migration.md](../code-coverage/raven-tls-migration.md)); URL `https://coverage-raven:8080`, database `SeoTester`. Set in the compose file, not `.env` | — | `Spark:RavenDb:Urls:0`, `Database`, `CertificateFile`, `CertificateKeyFile`, `ServerCertificateAuthorityFile` |
| **Data Protection keys** | Nothing: the `seotester_dataprotection-keys` volume. Deleting it signs everyone out once, and (with the MCP server, §4.4) makes the agents' signing keys unreadable | — | `Spark:DataProtection:KeysPath` (compose) |
| Public origin | Nothing: `https://seotester.mintplayer.com` in `appsettings.Production.json`. ⚠️ Also the **passkey relying-party id** (D46): never change it once passkeys exist | — | `Spark:Auth:PublicBaseUrl` |

**VAPID keys** (the format Spark expects: the uncompressed P-256 point and the private scalar, base64url), on the VPS:

```bash
cd "$(mktemp -d)"
openssl ecparam -name prime256v1 -genkey -noout -out vapid.pem
printf 'VAPID_PUBLIC_KEY=';  openssl ec -in vapid.pem -pubout -outform DER 2>/dev/null | tail -c 65 | base64 -w0 | tr '+/' '-_' | tr -d '='; echo
printf 'VAPID_PRIVATE_KEY='; openssl ec -in vapid.pem -outform DER 2>/dev/null | head -c 39 | tail -c 32 | base64 -w0 | tr '+/' '-_' | tr -d '='; echo
shred -u vapid.pem
```

Paste the two lines into `/var/www/seotester/.env`. The app refuses to start with a malformed pair (it checks the keys
belong together), so a copy mistake shows at the deploy, not at the first push.

### 4.3 The operator on `/privacy` and `/terms` (PRD D47)

The privacy policy and the terms are server pages (`apps/SeoTester/SeoTester/Legal/`, English and Dutch, version and
effective date at the top). ⚠️ **The texts are a draft written from the code for you to review, not legal advice.**
Who operates the service is configuration, never the repository: until a **required** value is set the pages show
**"[to be completed]"** ("[nog in te vullen]") in its place, and production logs a warning at startup naming the keys.
Each value appears in both the English and the Dutch text, so prefer names and numbers over translated phrases.

| `.env` | Config key | Required | What |
|---|---|---|---|
| `LEGAL_OPERATOR_NAME` | `SeoTester:Legal:OperatorName` | yes | the controller's legal name (a person or a company) |
| `LEGAL_CONTACT_EMAIL` | `SeoTester:Legal:ContactEmail` | yes | for questions and privacy requests (shown as a mailto link) |
| `LEGAL_HOSTING_PROVIDER` | `SeoTester:Legal:HostingProvider` | yes | completes "The server is hosted by …" (provider and country, e.g. `Hetzner Online GmbH, Germany`) |
| `LEGAL_ADDRESS` | `SeoTester:Legal:Address` | no | postal address, one line; left out when empty |
| `LEGAL_ENTERPRISE_NUMBER` | `SeoTester:Legal:EnterpriseNumber` | no | KBO/BCE number; left out when empty |
| `LEGAL_COURT` | `SeoTester:Legal:Court` | no | completes "disputes go to …"; empty reads "the competent Belgian courts" ("de bevoegde Belgische rechtbanken") |

**Run as a private person (owner, 2026-10-11): leave the address and the enterprise number empty.** GDPR art. 13(1)(a)
asks for the controller's identity and contact details, which the name and the e-mail give. The geographic address and
the enterprise number come from the e-commerce rules (Book XII WER) for services normally provided for remuneration;
SeoTester is free and has no registered enterprise behind it. Once it charges, register the enterprise and fill both in.
Not legal advice.

Google's OAuth consent screen and X's app settings take the two URLs above. When a text changes in substance, raise
`LegalPages.Version` and `EffectiveDate` in the code.

### 4.4 MCP server (plan M9; not deployed yet)

SeoTester serves AI agents at `/mcp` ([mcp.md](mcp.md); `docs/spark_mcp_PRD.md` §4.7). It is its own OAuth authorization
server for them (`idp.ForMcp()`), next to personal access tokens. **No new secret and no `.env` entry:**

| What | Where it comes from | Config key (override only) |
|---|---|---|
| **Signing keys** of agent tokens | Generated at the first start into the `OidcKeys` collection of the `SeoTester` database, the private halves protected with the Data Protection key ring (the `seotester_dataprotection-keys` volume), rotated every 90 days by the app. No key file, no key in configuration (`**/oidc-signing-key*.json` is git-ignored). ⚠️ Back up the volume with the database: without it the app refuses to start ("signing key … cannot be decrypted"), and the only way out, deleting the `OidcKeys` documents, disconnects every agent | `Spark:IdentityProvider:Keys:*` |
| **Issuer** of agent tokens | `Spark:Auth:PublicBaseUrl` (`https://seotester.mintplayer.com`, `appsettings.Production.json`), set by `Program.cs` | `Spark:IdentityProvider:Issuer` |
| **Canonical URL** (every agent token's audience) | The public URL + `/mcp`: `https://seotester.mintplayer.com/mcp`. Permanent: users paste it into their agents | `Spark:Mcp:CanonicalUrl` |

The proxy needs nothing: Traefik's router sends every path of the host to the app and does not buffer `text/event-stream`
(the app also sends `X-Accel-Buffering: no`). The new public routes are `/mcp`, `/connect/*` and `/.well-known/*`
(`apps/SeoTester/SeoTester.Tests/RouteSnapshots/SeoTester.txt`). The identity provider's developer portal rights are
switched off in `security.json` (`"libraries": { "identity-provider": false }`), so nobody can register other OAuth
clients; agents register themselves (client metadata documents, or loopback-only dynamic registration, 20 an hour per
address).

**Rate limiting** (owner decision 2026-10-12): the app runs ASP.NET's rate limiter (`spark.AddRateLimiter()` in
`Program.cs`), so the identity provider's own budgets apply: 120 requests a minute per address on its machine endpoints
(token, PAR, device authorization, revocation, userinfo and client registration; `Spark:IdentityProvider:RateLimits`), on
top of Spark's 150 per 10 s per address on `/spark` and `/connect` (`Spark:RateLimiter`). Over a budget the answer is
429. The address is the client's: Spark core takes it from Traefik's `X-Forwarded-For` (`Spark:ForwardedHeaders`).
Nothing to configure; `/mcp` keeps its own per-user and per-address quota (`Spark:Mcp:Quota`).

After the deploy:

```bash
curl -sS https://seotester.mintplayer.com/.well-known/oauth-protected-resource/mcp   # resource = …/mcp, authorization_servers = the issuer
curl -sS https://seotester.mintplayer.com/.well-known/oauth-authorization-server     # issuer = https://seotester.mintplayer.com
```

Then, from a workstation: `claude mcp add --transport http seotester https://seotester.mintplayer.com/mcp`, `/mcp` →
Authenticate, sign in, and run each documented example of [mcp.md](mcp.md) once (plan M9); remove mcp.md's Draft banner
afterwards.

## 5. Post-deploy checklist (measurements deferred to M12)

Record each result in PRD §12 ("M12 deploy") and close its question in §11.

### Q-M5a: `/spark/live` through the real proxy

1. The upgrade passes Traefik (expect `HTTP/1.1 101 Switching Protocols`; a 401/403 means the socket wants a signed-in
   session, then use step 2 only):

   ```bash
   curl -sS -i -N --http1.1 --max-time 5 \
     -H "Connection: Upgrade" -H "Upgrade: websocket" -H "Sec-WebSocket-Version: 13" \
     -H "Sec-WebSocket-Key: $(openssl rand -base64 16)" -H "Origin: https://seotester.mintplayer.com" \
     https://seotester.mintplayer.com/spark/live | head -1
   ```
2. Idle survival: sign in, open a site page, DevTools → Network → *WS* → `live`. Leave the tab untouched for **5
   minutes**: the socket stays open (no new `live` row), and *Messages* shows the server's ping every 30 s.
3. Live progress: *Run audit* on a site: the run page's progress moves without a reload.
4. Traefik's own timeouts, for the record: `docker inspect traefik --format '{{join .Args " "}}' | tr ' ' '\n' | grep -i timeout`
   (none set = the defaults, idle 180 s, above the 30 s heartbeat).

### Q-M6a: Web Push on Firefox, Edge and Safari

With `VAPID_*` set: on each of **desktop Firefox**, **desktop Edge** and an **iPhone with SeoTester added to the Home
Screen** (iOS 16.4+; push works only from the installed app): sign in, allow notifications (account page or the bell),
then start an audit and close the tab/app. Expect the "Audit of … finished" push within a minute of the run ending, and
a click opening the run. Delivery failures show in the logs: `docker compose --env-file .env logs seotester-app | grep -i webpush`.
Per device, record delivered / not, and the push service (`$OP -X POST "https://coverage-raven:8080/databases/SeoTester/queries" -H "Content-Type: application/json"
--data '{"Query":"from PushSubscriptions select Service, Device"}'`; the endpoints themselves are capability URLs: never print them).

### Q-M9b: the TLS probe from the Debian image

1. The image's TLS stack and HTTP/3 support:

   ```bash
   cd /var/www/seotester
   docker compose --env-file .env exec seotester-app bash -c 'head -2 /etc/os-release; ls /usr/lib/x86_64-linux-gnu | grep -E "^libssl|msquic" || true'
   ```
   Expect Debian and `libssl.so.3`; no `libmsquic` means HTTP/3 is reported from `Alt-Svc` only (PRD §1).
2. A real audit of a site you know (e.g. `https://mintplayer.com`): in the report, `security.tls13`,
   `security.tls-legacy` and `security.certificate-expiry` are **Pass/Fail with values**, not "Error" or "not measured".
   A site known to still accept TLS 1.0 on port 443 shows `security.tls-legacy` failing (the raw hello does not depend
   on OpenSSL).

### Q-M10b: the Chromium container on Debian

```bash
cd /var/www/seotester
docker image ls --format '{{.Repository}}:{{.Tag}} {{.Size}}' | grep seotester                  # both image sizes
docker compose --env-file .env exec seotester-app du -sh /app/.playwright                      # the driver in the app image
docker network inspect seotester_seo-browser --format 'internal={{.Internal}}'                 # internal=true
# Chromium reaches nothing (expect "blocked" and a failed TCP connect):
docker compose --env-file .env exec seo-chromium node -e "fetch('https://example.com').then(()=>console.log('ESCAPED'),e=>console.log('blocked',e.cause?.code??e.message))"
docker compose --env-file .env exec seo-chromium bash -c 'exec 3<>/dev/tcp/1.1.1.1/443 && echo ESCAPED || echo blocked'
docker compose --env-file .env exec seo-chromium bash -c 'getent hosts coverage-raven || echo "no route to the database"'
# During a run's render stage (start an audit, wait for the phase "render"):
docker stats --no-stream seotester-seo-chromium-1 seotester-seotester-app-1
docker compose --env-file .env exec seo-chromium df -h /dev/shm
```

Then the run's rendered checks (accessibility, phone layout, rendered vs raw) are measured, not "the headless browser is
not configured" or errors, and *Download PDF* produces a PDF. If Chromium's peak is far below or near `mem_limit`, or
`/dev/shm` fills, adjust `mem_limit` / `shm_size` in `apps/SeoTester/docker-compose.yml` and record the numbers.

### Also check once

- **The fetcher (PRD D54):** `docker compose --env-file .env ps` shows `seo-fetcher` healthy; an audit of a site
  completes; `docker inspect seotester-seo-fetcher-1 -f '{{json .NetworkSettings.Networks}}'` names only `seo-fetch` and
  `seo-egress`; from inside it RavenDB does not resolve
  (`docker compose --env-file .env exec seo-fetcher bash -c "exec 3<>/dev/tcp/coverage-raven/8080" ; echo $?` fails).
  Record the per-request latency of a render (Q-M10b's run) beside D54's loopback figures.

- **The legal pages:** `https://seotester.mintplayer.com/privacy` and `/terms` (and `?lang=nl`) show no
  "[to be completed]", and `docker compose --env-file .env logs seotester-app | grep -i "to be completed"` is empty.

- **The shared network after the migration (§3.0).** CodeCoverage still sends mail (sign in there once with a provider
  that mails, or check `docker logs code-coverage-coverage-smtp-1 --since 1h | grep status=sent`), and on the next
  CodeCoverage deploy SeoTester reconnects to RavenDB without a restart
  (`docker compose --env-file .env logs --since 10m seotester-app | grep -i raven`).
- **Monitoring:** set a verified site to *Weekly*; within 15 minutes a run starts (abuse log: *AuditStarted*, detail
  "scheduled") and the site shows the next scheduled audit a week on.

## 6. Operations

| Task | Command (in `/var/www/seotester`) |
|---|---|
| Logs | `docker compose --env-file .env logs --tail 200 -f seotester-app` |
| Restart the app | `docker compose --env-file .env restart seotester-app` |
| Restart the browser | `docker compose --env-file .env restart seo-chromium` (a render in progress fails and is retried by its queue) |
| Change a secret | edit `.env`, then `docker compose --env-file .env up -d seotester-app` |
| Status | `docker compose --env-file .env ps` |
