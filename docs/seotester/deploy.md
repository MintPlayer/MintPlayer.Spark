# SeoTester: deploy runbook

How **https://seotester.mintplayer.com** runs on the VPS it shares with coverage.mintplayer.com, what the owner sets up
once, which credentials to register, and what to measure after the first deploy. Spec: [seo_analyzer_PRD.md](../seo_analyzer_PRD.md)
§1 "Hosting constraints", D3, D6, M12. The app itself: [README.md](README.md). CodeCoverage's hosting, which this
mirrors and shares: [docs/code-coverage/hosting.md](../code-coverage/hosting.md) and `apps/CodeCoverage/README.md`.

Status (2026-10-10): the files are written and checked (`tools/verify-seotester-deploy.test.mjs`, `docker compose config`);
**nothing has been deployed**. Every step below is the owner's.

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
  └── network code-coverage_coverage-internal ──┘  └────────────────────────────────────┘
```

| Service | Image | Networks | Notes |
|---|---|---|---|
| `seotester-app` | `ghcr.io/mintplayer/seotester:master` (pull-only, built by `seotester-deploy.yml`) | `web`, `code-coverage_coverage-internal`, `seo-browser` | `mem_limit 1g`; `GET /health` healthcheck, start period 300 s; volume `dataprotection-keys`; Traefik labels for `seotester.mintplayer.com` |
| `seo-chromium` | `ghcr.io/mintplayer/seotester-chromium:1.63.0` (= Microsoft.Playwright's version) | `seo-browser` **only** | `shm_size 512m`, `mem_limit 1g`, `pids_limit 512`, `init`, no capabilities, no port published; TCP healthcheck on 3000 |

- **Database:** CodeCoverage's RavenDB server (`coverage-raven`, Community licence), database **`SeoTester`**, created on
  the first start (`Spark__RavenDb__EnsureDatabaseCreated=true`). Unsecured, reachable only on
  `code-coverage_coverage-internal`, so **no certificate**: Spark's `Spark:RavenDb` has `Urls`, `Database` and
  `EnsureDatabaseCreated`, and the compose file sets them (`http://coverage-raven:8080`).
- **Mail:** CodeCoverage's relay `coverage-smtp:587` (no credentials; `Security=None` as CodeCoverage). It must accept
  the sender domain `seotester.mintplayer.com` (§3.2).
- **Why the shared server and relay:** one RavenDB process and one mail reputation on a small VPS, both already
  operated (licence activation, DKIM, PTR). The cost: SeoTester starts only when the CodeCoverage stack is up (the deploy
  checks the network), and CodeCoverage's maintenance windows are SeoTester's too.
- **The Chromium container** can reach nothing: `seo-browser` is `internal: true`, and Chromium itself never resolves a
  name or opens a connection (`--host-resolver-rules=MAP * ~NOTFOUND`, a dead proxy; SeoTester fulfils every request
  through its SSRF-safe client, PRD §6). It can reach `seotester-app:8080` on that network, which only matters if both
  layers fail.

The files: `apps/SeoTester/docker-compose.yml`, `apps/SeoTester/.env.example`, `.github/workflows/seotester-deploy.yml`
(push to master or *Run workflow*), `.github/workflows/seotester-image-check.yml` (pull requests: builds both images, no
push). `tools/verify-seotester-deploy.test.mjs` (`npm run test:tools`) checks them against each other and the code.

## 2. The deploy workflow

`seotester-deploy.yml`, on a push to `master` touching SeoTester or a library it references (the path list equals the
Dockerfile's COPY lines and the csproj graph; the tools test keeps the three equal), or *Run workflow*:

1. **build-and-push:** `apps/SeoTester/SeoTester/Dockerfile` → `ghcr.io/mintplayer/seotester:master` (+ the sha tag),
   provenance attestation, makes the package public (best effort).
2. **build-chromium:** `apps/SeoTester/chromium/Dockerfile` → `ghcr.io/mintplayer/seotester-chromium:<Playwright version>`
   (read from the Dockerfile's `FROM`).
3. **deploy** (master only): SSH with the **same repository secrets as CodeCoverage's deploy** (`VPS_HOST`,
   `VPS_USERNAME`, `VPS_SSH_KEY`, `VPS_PORT`, `VPS_SSH_KEY_PASSPHRASE`; `GITHUB_TOKEN` is automatic): no new secret.
   On the VPS: refuses without `/var/www/seotester/.env` or without the network `code-coverage_coverage-internal`;
   downloads `docker-compose.yml` from master; `docker compose pull`, `down --remove-orphans`, `up -d`; prunes images;
   then waits for `GET /health` = 200 inside the container (36 × 10 s, the healthcheck's start period + 1 min).

Bumping Playwright: change `Microsoft.Playwright` in `SeoTester.csproj`, the chromium Dockerfile's `FROM` and
`playwright-core@`, and the tag in `docker-compose.yml`, together (the tools test fails otherwise).

## 3. First-time setup on the VPS (owner)

Run on the VPS as the deploy user unless noted. None of these print a secret.

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
     'apk add --no-cache opendkim-utils && opendkim-genkey -b 2048 -d seotester.mintplayer.com -s mail -D /out'
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

Deploys never write `.env`; the workflow refuses to deploy without it.

### 3.4 The database and its expiration

Nothing to create by hand: the first start creates `SeoTester` on `coverage-raven` and switches **document expiration** on
at the Community licence's floor, **every 36 h** (`DeleteFrequencyInSec` 129600; Spark's `Spark:Expiration`, which
refuses anything shorter). Page details (7 d), frontiers (2 d), cool-downs, notifications, invitations and the abuse log
(1 y) expire through it; every read hides expired documents, so the 36 h lag is invisible. Check after the first start:

```bash
NET=code-coverage_coverage-internal
docker run --rm --network $NET curlimages/curl:8.11.1 -sS "http://coverage-raven:8080/databases?namesOnly=true"
docker run --rm --network $NET curlimages/curl:8.11.1 -sS "http://coverage-raven:8080/admin/databases?name=SeoTester" \
  | grep -o '"Expiration":{[^}]*}'
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
docker run --rm --network code-coverage_coverage-internal curlimages/curl:8.11.1 -sS -X PATCH \
  "http://coverage-raven:8080/databases/SeoTester/queries" -H "Content-Type: application/json" \
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
| X | developer.x.com → Projects & Apps → the app → *User authentication settings*: **OAuth 2.0**, type *Web App*, **Request email from users** on (X requires a privacy policy URL and a terms URL, which SeoTester does not have yet: PRD Q-M12a) | `https://seotester.mintplayer.com/signin-twitter` | `users.read`, `tweet.read`, `users.email`; the **OAuth 2.0 Client ID and Client Secret**, not the 1.0a API key | `X_CLIENT_ID`, `X_CLIENT_SECRET` | `Spark:Auth:Providers:Twitter:…` |
| Google | console.cloud.google.com → APIs & Services → *OAuth consent screen* (External, app name SeoTester, scopes below), then Credentials → *OAuth client ID* → Web application | `https://seotester.mintplayer.com/signin-google` | `openid`, `profile`, `email` | `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | `Spark:Auth:Providers:Google:…` |

For local development, add `https://localhost:5013/signin-…` as a second redirect URI on a development app instead of
the production one (README "Running it locally").

### 4.2 Everything else

| What | How to get it | `.env` | Config key |
|---|---|---|---|
| **PageSpeed Insights / CrUX** | The key already in your local user secrets as `SeoTester:PageSpeed:ApiKey` (Google Cloud → APIs & Services → Credentials). Restrict it: *API restrictions* = PageSpeed Insights API; *Application restrictions* = IP addresses `188.245.190.60` (and the VPS's IPv6) | `PAGESPEED_API_KEY` | `SeoTester:PageSpeed:ApiKey` |
| **Web Push (VAPID)** | Generate once (below). `VAPID_SUBJECT` is `mailto:` your address. Changing the pair later drops every browser's subscription | `VAPID_PUBLIC_KEY`, `VAPID_PRIVATE_KEY`, `VAPID_SUBJECT` | `Spark:Notifications:WebPush:PublicKey` / `PrivateKey` / `Subject` |
| **SMTP** | Nothing to register: CodeCoverage's relay, once §3.2 is done. Overrides only if you relay elsewhere | `MAIL_HOST`, `MAIL_PORT`, `MAIL_FROM_ADDRESS`, `MAIL_FROM_NAME` (all optional) | `Spark:Mail:Smtp:Host` / `Port`, `Spark:Mail:From:Address` / `Name` |
| **RavenDB** | Nothing: `http://coverage-raven:8080`, database `SeoTester`, no certificate (unsecured on the internal network). Set in the compose file, not `.env` | — | `Spark:RavenDb:Urls:0`, `Spark:RavenDb:Database` |
| **Data Protection keys** | Nothing: the `seotester_dataprotection-keys` volume. Deleting it signs everyone out once | — | `Spark:DataProtection:KeysPath` (compose) |
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
Per device, record delivered / not, and the push service (`docker run --rm --network code-coverage_coverage-internal
curlimages/curl:8.11.1 -sS -X POST "http://coverage-raven:8080/databases/SeoTester/queries" -H "Content-Type: application/json"
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

- **CodeCoverage's deploy with SeoTester running.** Its `docker compose down` cannot remove
  `code-coverage_coverage-internal` while `seotester-app` is attached; Compose reports the network "still in use" and
  `up` reuses it. Confirm on the next CodeCoverage deploy that its job stays green and SeoTester reconnects to RavenDB
  without a restart (`docker compose logs --since 10m seotester-app | grep -i raven`). If the job fails on that line, the
  fix is a network both stacks declare as external (a compose change to CodeCoverage).
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
