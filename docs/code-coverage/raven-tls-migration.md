# RavenDB over TLS with client certificates — design and one-time migration of the live server

> **Proven in Docker on 2026-10-11** (§8; SeoTester PRD Q-D56a, §12 "Q-D56a"): the pinned
> `ravendb/ravendb:7.1.10-ubuntu.22.04-x64` with this page's settings and certificates, both compose files brought up
> locally with stand-ins for the apps, and §4.2, §4.4, §4.6, §4.7, §4.8 and §4.9 run as written. What could not be
> measured is said so in §7 and §8.

Owner decision 2026-10-10 (PRD D55, D56): minimal network exposure, and a secured RavenDB server shared by CodeCoverage
and SeoTester. Files: [`apps/CodeCoverage/docker-compose.yml`](../../apps/CodeCoverage/docker-compose.yml),
[`apps/SeoTester/docker-compose.yml`](../../apps/SeoTester/docker-compose.yml), both deploy workflows;
`tools/verify-backend-networks.test.mjs` holds them to this page.

## 1. Networks: who reaches whom

Every backend network is `internal: true` (no route out) and pairwise; RavenDB and the mail relay never share one.

| Network | Members | Route out |
|---|---|---|
| `web` (external, Traefik's) | `coverage-app`, `seotester-app` | yes (the apps' egress) |
| `coverage-db` | `coverage-app`, `coverage-raven`, `coverage-raven-license` | no |
| `coverage-mail` | `coverage-app`, `coverage-smtp` | no |
| `coverage-smtp-egress` | `coverage-smtp` | yes (the relay delivers to recipients' MX) |
| `seo-raven` (external, created `--internal` by either deploy) | `coverage-raven`, `seotester-app` | no |
| `seo-mail` (external, created `--internal` by either deploy) | `coverage-smtp`, `seotester-app` | no |
| `seo-browser` | `seotester-app`, `seo-chromium` | no |

Measured (§8): every connection the table allows succeeds, every other one fails, both ways. One thing the table
implies and is worth stating: the two apps can reach each other over `web`, the Traefik network every public stack
joins; that predates D55 and is not a backend network.

**RavenDB has no egress, on purpose.** Cited from `Raven.Server` 7.1 (`Commercial/LicenseManager`, decompiled from the
7.1.3 embedded server): activating a licence that is unexpired and of version ≥ 6 calls nothing outside the server
(`ActivateAsync` only fetches an updated licence from `api.ravendb.net` when the given one is expired or older than v6,
and the Let's Encrypt update only runs in `Setup.Mode=LetsEncrypt`). Without egress, the 24-hour lease timer
(`LeaseLicense`, `throwOnError: false`) fails with a *"Failed to lease license"* warning alert and nothing else. The
cost: a licence renewal is not pulled automatically, and `POST /admin/license/renew` (run from inside `coverage-raven` on
2026-09-28) no longer works; renew at ravendb.net, put the mailed JSON in `raven-license.json` and re-run the one-shot.

## 2. Certificates

| Certificate | Clearance on the server | Lives on the VPS | Mounted into |
|---|---|---|---|
| CA (`ca.crt`/`ca.key`, **20 years**) | — (signs the others) | `ca.crt` beside each stack; **`ca.key` in `/root/raven-ca/`, root-only `0400`** (R1: the nightly renewal signs with it), a copy in the owner's password manager | `ca.crt`: both apps, the licence one-shot and the renewal's curl calls; `ca.key`: nothing (read by the host script only) |
| Server (`server.pfx`, 825 days, SAN `DNS:coverage-raven`, EKU server + client) | — | `/var/www/code-coverage/raven-certs/server.pfx` | `coverage-raven` only |
| Operator (`operator.crt`/`.key`) | **Cluster admin**, through `RAVEN_Security_WellKnownCertificates_Admin` (its thumbprint) | `/var/www/code-coverage/raven-operator/` | the licence one-shot and the renewal script only (R8: no browser, no Studio in production) |
| `codecoverage` | `ValidUser`, **`Coverage`: Admin**, nothing else | `/var/www/code-coverage/raven-certs/` | `coverage-app` only |
| `seotester` | `ValidUser`, **`SeoTester`: Admin**, nothing else | `/var/www/seotester/raven-certs/` | `seotester-app` only |

Why database **Admin** and not ReadWrite: Spark deploys its indexes and configures expiration at startup, both
database-admin operations. Database Admin is still scoped to that one database (spike: refused on the other database,
refused creating or deleting a database, listing or registering certificates, activating a licence).

The apps read their certificate from configuration (Spark core, `SparkRavenDbSecurity`):
`Spark__RavenDb__CertificateFile`, `Spark__RavenDb__CertificateKeyFile` (PEM), `Spark__RavenDb__ServerCertificateAuthorityFile`
(the CA: the server certificate must chain to it and its name must match `coverage-raven`). `EnsureDatabaseCreated` is
`false`: creating a database is cluster admin; the operator creates them.

## 3. Files, owners and permissions on the VPS

Directories `0750` root-owned; nothing here is ever in the repository or printed.

| File | Owner (uid) | Mode |
|---|---|---|
| `/var/www/code-coverage/raven-certs/server.pfx` | RavenDB's container user: **999** (`ravendb`; measured with `docker run --rm --entrypoint id ravendb/ravendb:7.1.10-ubuntu.22.04-x64`) | `0400` |
| `/var/www/code-coverage/raven-certs/ca.crt`, `codecoverage.crt` | root | `0644` |
| `/var/www/code-coverage/raven-certs/codecoverage.key` | the app's user: **1654** (`app`; measured on `ghcr.io/mintplayer/codecoverage:master`) | `0400` |
| `/var/www/code-coverage/raven-operator/operator.crt` | root | `0644` |
| `/var/www/code-coverage/raven-operator/operator.key` | curl's user: **100** (`curl_user`, group 101; measured on `curlimages/curl:8.11.1`) | `0400` |
| `/var/www/seotester/raven-certs/ca.crt`, `seotester.crt` | root | `0644` |
| `/var/www/seotester/raven-certs/seotester.key` | SeoTester's user: 1654 (its Dockerfile says `USER app` on the same .NET base; the image is not published yet, so check it with the same command before installing) | `0400` |
| `/var/www/code-coverage/.env`: `RAVEN_SERVER_CERTIFICATE_PASSWORD`, `RAVEN_OPERATOR_THUMBPRINT` | (as today) | `0600` |

The relay (`boky/postfix:v4.3.0`) runs as root (measured) and mounts no certificate; its DKIM key stays owned 101:104 as
the README says. The modes above could not be measured on Docker Desktop for Windows, whose bind mounts ignore host
ownership; the uids are what the files must be readable by.

The CodeCoverage deploy refuses, before touching the stack, when any of its files is missing or a variable is unset;
the SeoTester deploy likewise for its three files.

## 4. The migration, step by step

**Expected downtime: about 5 minutes** for CodeCoverage (steps 4.5–4.8: RavenDB restarts secured, two certificate
registrations, the app's ordinary start), plus the cold copy in 4.4, whose length is the size of `code-coverage_raven-data`
(time `du -sh` of the volume first). Then one ordinary deploy restart when the PR merges (4.10). SeoTester has not been
deployed yet, so it has no downtime; it is deployed after this (deploy.md §3.0).

Done **before merging the PR**, by hand, with the PR's compose file, so that the deploy after the merge finds
everything in place. Run as root on the VPS, in `bash`.

### 4.1 Hot backup (no downtime)

The README's *Backup* commands in their pre-migration form, as its comment says (the server is still unsecured):
`NET=code-coverage_coverage-internal`, `MNT=` and `AUTH=` empty, `http://` for `https://`. The stats file and the
`.ravendbdump` with `CompareExchange`. Keep the stamp:

```bash
STAMP=$(date -u +%Y%m%dT%H%M%SZ); echo "$STAMP"
# then apps/CodeCoverage/README.md "Backup", both docker run commands, with that STAMP
```

### 4.2 Generate the certificates (no downtime)

```bash
install -d -m 0700 /root/raven-ca && cd /root/raven-ca && umask 077
openssl rand -hex 24 > server.pfx.pass

openssl req -x509 -newkey rsa:4096 -sha256 -days 7300 -nodes \
  -keyout ca.key -out ca.crt -subj "/CN=MintPlayer RavenDB CA" \
  -addext "basicConstraints=critical,CA:TRUE,pathlen:0" -addext "keyUsage=critical,keyCertSign,cRLSign"

openssl req -x509 -newkey rsa:2048 -sha256 -days 825 -nodes \
  -CA ca.crt -CAkey ca.key -keyout server.key -out server.crt -subj "/CN=coverage-raven" \
  -addext "subjectAltName=DNS:coverage-raven" \
  -addext "basicConstraints=critical,CA:FALSE" -addext "keyUsage=critical,digitalSignature,keyEncipherment" \
  -addext "extendedKeyUsage=serverAuth,clientAuth"
openssl pkcs12 -export -inkey server.key -in server.crt -certfile ca.crt -out server.pfx -passout file:server.pfx.pass

for name in operator codecoverage seotester; do
  openssl req -x509 -newkey rsa:2048 -sha256 -days 825 -nodes \
    -CA ca.crt -CAkey ca.key -keyout "$name.key" -out "$name.crt" -subj "/CN=$name" \
    -addext "basicConstraints=critical,CA:FALSE" -addext "keyUsage=critical,digitalSignature,keyEncipherment" \
    -addext "extendedKeyUsage=clientAuth"
done
openssl verify -CAfile ca.crt server.crt operator.crt codecoverage.crt seotester.crt   # all OK
```

(`openssl req -x509 -CA` needs OpenSSL ≥ 3.0, which Ubuntu 22.04 and later have.) **`ca.key` stays here** (R1),
`chmod 0400 ca.key`; copy it to the password manager as well (scp to the owner's machine, never printed). No browser
certificate (R8).

### 4.3 Put the files in place (no downtime)

```bash
RAVEN_UID=$(docker run --rm --entrypoint id ravendb/ravendb:7.1.10-ubuntu.22.04-x64 -u)
APP_UID=$(docker run --rm --entrypoint id ghcr.io/mintplayer/codecoverage:master -u)
CURL_UID=$(docker run --rm --entrypoint id curlimages/curl:8.11.1 -u)
SEO_UID=$APP_UID   # same .NET base image; check with ghcr.io/mintplayer/seotester:master once it is published

cd /root/raven-ca
install -d -m 0750 /var/www/code-coverage/raven-certs /var/www/code-coverage/raven-operator /var/www/seotester/raven-certs
install -m 0400 -o "$RAVEN_UID" server.pfx        /var/www/code-coverage/raven-certs/server.pfx
install -m 0644 ca.crt codecoverage.crt           /var/www/code-coverage/raven-certs/
install -m 0400 -o "$APP_UID" codecoverage.key    /var/www/code-coverage/raven-certs/codecoverage.key
install -m 0644 operator.crt                      /var/www/code-coverage/raven-operator/
install -m 0400 -o "$CURL_UID" operator.key       /var/www/code-coverage/raven-operator/operator.key
install -m 0644 ca.crt seotester.crt              /var/www/seotester/raven-certs/
install -m 0400 -o "$SEO_UID" seotester.key       /var/www/seotester/raven-certs/seotester.key

# The two .env values, written from the files, never echoed:
cd /var/www/code-coverage
printf 'RAVEN_SERVER_CERTIFICATE_PASSWORD=%s\n' "$(cat /root/raven-ca/server.pfx.pass)" >> .env
printf 'RAVEN_OPERATOR_THUMBPRINT=%s\n' "$(openssl x509 -in raven-operator/operator.crt -noout -fingerprint -sha1 | sed 's/.*=//; s/://g')" >> .env
printf 'CERT_ALERT_EMAIL=%s\n' 'info@mintplayer.com' >> .env   # R6: the renewal's failure mail; comma-separated list allowed
# R1: ca.key stays. Every OTHER key now lives only where it is mounted:
cd /root/raven-ca && shred -u server.pfx.pass server.key operator.key codecoverage.key seotester.key server.pfx
```

The directories must be traversable by the container users: `0750` root-owned works because Docker bind-mounts single
files by path (the daemon, not the container user, opens the directory). `/root/raven-ca` (`0700`) keeps the public
`.crt` files and `ca.key` (`0400`); the deploy re-asserts those modes.

### 4.4 The window opens: stop, cold copy (the rollback point)

```bash
cd /var/www/code-coverage
cp docker-compose.yml docker-compose.yml.pre-tls
docker compose --env-file .env down          # removes the old coverage-internal network too
docker run --rm -v code-coverage_raven-data:/data:ro -v /var/backups/coverage-raven:/out alpine:3.20 \
  tar -C /data -czf "/out/raven-data-$STAMP.tgz" .
```

### 4.5 The new compose file and the networks

From the PR branch (or `scp` it from a checkout of the PR if raw.githubusercontent.com refuses):

```bash
curl -sf -o docker-compose.yml \
  https://raw.githubusercontent.com/MintPlayer/MintPlayer.Spark/feat/268-seotester/apps/CodeCoverage/docker-compose.yml
docker network rm mintplayer-backend 2>/dev/null || true   # D48's network, if it was ever created
for net in seo-raven seo-mail; do
  docker network inspect "$net" > /dev/null 2>&1 || docker network create --internal "$net" > /dev/null
done
docker compose --env-file .env config -q        # fails here, not later, on a missing variable
docker compose --env-file .env up -d coverage-raven
docker compose --env-file .env ps coverage-raven  # wait for (healthy)
```

### 4.6 Register the app certificates and create SeoTester's database (operator)

```bash
cd /var/www/code-coverage
OP="docker run --rm --network code-coverage_coverage-db \
  -v $PWD/raven-operator/operator.crt:/op.crt:ro -v $PWD/raven-operator/operator.key:/op.key:ro -v $PWD/raven-certs/ca.crt:/ca.crt:ro \
  curlimages/curl:8.11.1 -sS -f --cert /op.crt --key /op.key --cacert /ca.crt"
R=https://coverage-raven:8080

register() {   # name, database, certificate file (public; base64 of the DER)
  local b64; b64=$(openssl x509 -in "$3" -outform der | base64 -w0)
  $OP -X PUT "$R/admin/certificates" -H 'Content-Type: application/json' \
    --data "{\"Name\":\"$1\",\"Certificate\":\"$b64\",\"SecurityClearance\":\"ValidUser\",\"Permissions\":{\"$2\":\"Admin\"}}"
}
register codecoverage Coverage  raven-certs/codecoverage.crt
register seotester    SeoTester /var/www/seotester/raven-certs/seotester.crt
$OP -X PUT "$R/admin/databases?replicationFactor=1" -H 'Content-Type: application/json' --data '{"DatabaseName":"SeoTester"}'

$OP "$R/admin/certificates" | grep -o '"Name":"[^"]*"' | sort -u   # codecoverage, seotester, "Server Certificate" (the well-known operator is not listed)
$OP "$R/databases?namesOnly=true"                           # Coverage, SeoTester
```

Each `PUT` answers 201 (measured on 7.1.10; re-registering the same certificate also answers 201).

### 4.7 Start the rest

```bash
docker compose --env-file .env up -d           # the licence one-shot, the relay, the app
docker compose --env-file .env ps -a           # coverage-raven-license: Exited (0); the others healthy
```

### 4.8 Verify, then the window is closed

```bash
# The licence is still there (it lives in raven-data), and the one-shot re-applied it over TLS:
$OP "$R/license/status" | grep -o '"Type":"[^"]*"\|"Expired":[a-z]*'
# The app is up against the secured server (the deploy's readiness check):
docker compose --env-file .env exec -T coverage-app bash -c "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3 && head -1 <&3"
# Unsecured access is gone, and the app certificate cannot reach SeoTester's database (expect 000 and 403):
docker run --rm --network code-coverage_coverage-db curlimages/curl:8.11.1 -s -o /dev/null -w '%{http_code}\n' http://coverage-raven:8080/databases
docker run --rm --network code-coverage_coverage-db --user 0 \
  -v $PWD/raven-certs:/c:ro curlimages/curl:8.11.1 -s -o /dev/null -w '%{http_code}\n' \
  --cert /c/codecoverage.crt --key /c/codecoverage.key --cacert /c/ca.crt "$R/databases/SeoTester/stats"
```

Then open https://coverage.mintplayer.com, sign in, open a repository. When all of this passed, delete the cold copy
(R10; the `.ravendbdump` of 4.1 stays as the fallback): `rm -f /var/backups/coverage-raven/raven-data-$STAMP.tgz`,
and check the renewal sees all four certificates: `raven-cert-renew --status` (installed by the deploy at 4.10; before
that, run `apps/CodeCoverage/ops/raven-cert-renew.sh --status` from a copy).

### 4.9 Rollback (any step from 4.5 on)

The data is untouched by TLS (the certificates live in the cluster state inside `raven-data`, harmless to an unsecured
server), so rolling back is the old compose file:

```bash
cd /var/www/code-coverage
docker compose --env-file .env down
cp docker-compose.yml.pre-tls docker-compose.yml
docker compose --env-file .env up -d
```

Only if the data itself is damaged (not expected): `down`, then restore the cold copy into the volume
(`docker run --rm -v code-coverage_raven-data:/data -v /var/backups/coverage-raven:/in alpine:3.20 sh -c 'rm -rf /data/* && tar -C /data -xzf /in/raven-data-$STAMP.tgz'`),
then `up -d`. The `.ravendbdump` from 4.1 is the second line (README *Backup*, restore). Do not merge the PR until 4.8
passed: after the merge, the deploy would bring back the secured compose file.

### 4.10 Merge

The deploy finds the files and variables (its pre-checks), creates nothing new, and does its ordinary `down`/`up`
(one more short restart). Then SeoTester's first deploy (deploy.md §3.0).

## 5. Day to day

- **Admin calls** (backup, stats, licence status): the `OP` form of 4.6 on `code-coverage_coverage-db`, `https://`.
  The README's *Backup* commands are written that way.
- **No Studio in production** (R8, owner 2026-10-11: "No, never"). If it is ever needed, issue a ClusterAdmin
  certificate for it then, with the 4.2 command and the 4.6 `register` (clearance `ClusterAdmin`).
- **Licence:** replace `raven-license.json`, then `docker compose --env-file .env up coverage-raven-license`.

## 6. Renewal: automatic (`raven-cert-renew`)

Nothing to do by hand. `apps/CodeCoverage/ops/raven-cert-renew.sh`, installed by every CodeCoverage deploy as
`/usr/local/sbin/raven-cert-renew` with `raven-cert-renew.timer` (from the deployed commit, root-owned), runs nightly
at 04:00 Europe/Brussels. When any of the four leaves (server, operator, codecoverage, seotester) has 30 days or fewer
left, it re-issues **all four with new keys** (825 days) and:

1. registers the new app certificates **beside** the old ones, with the still-valid operator;
2. swaps `server.pfx` and the operator files, writes the new `RAVEN_OPERATOR_THUMBPRINT` into `.env`, recreates
   `coverage-raven` (≈30 s; a bind-mounted file stays pinned to its old inode until the container is recreated) and
   checks the new operator is an administrator, **rolling back** to the old files and `.env` if not;
3. swaps each app's files and recreates `coverage-app` (`/health/ready` 200) and `seotester-app` (`/health` 200);
4. removes the old app registrations (only names `codecoverage*`/`seotester*` whose thumbprint is not installed, and
   never with an incomplete keep-list).

`--scheduled` (the timer) tries 3 times 10 minutes apart; when all fail it mails `CERT_ALERT_EMAIL` through
`coverage-smtp` and tries again the next night, so a lasting problem mails every night. It also mails once a night
while the CA has under a year left; the CA (20 years) is the one thing renewed by hand: a new CA means re-issuing
everything, so plan it then.

By hand: `raven-cert-renew --status` (expiry dates, whether due), `raven-cert-renew --force` (renew now, e.g. after a
suspected key leak), `journalctl -u raven-cert-renew` (the logs).

**Security of the mechanism** (owner, 2026-10-11: "Make sure not to introduce vulnerabilities"): RavenDB gets no new
network or port, every call is a throwaway `curlimages/curl` container on the internal `coverage-db` (as curl's own
user, not root), the mail goes over the internal `coverage-mail`; `umask 077` for everything the script creates and
every key of a run shredded on exit; the pfx password reaches openssl through its environment only, never argv or a
log; thumbprints must be 40 hex characters and alert addresses plain addresses before they reach `sed`, a URL, `.env`
or a mail header; the deploy downloads into a fresh `mktemp -d` (no fixed `/tmp` names) and installs root-owned; the
service runs with `NoNewPrivileges` and `UMask=0077`; `flock` refuses a second concurrent run.

### Decisions (owner, 2026-10-11, grilled)

| # | Decision |
|---|---|
| R1 | The CA key lives on the VPS (`/root/raven-ca/ca.key`, root, `0400`) plus the password manager: unattended renewal must sign. Root on the VPS already holds the data, the leaf keys and `.env`, so it adds little. CA 20 years. |
| R2 | Leaves 825 days, renewed at 30 days left (Community licence: no duration limit, §7; the 4-month rule is the Developer licence's only). |
| R3 | New keys at every renewal; app certificates registered explicitly beside the old ones (not relying on RavenDB trusting a reused key). |
| R4 | All four renewed together: one RavenDB restart and one app restart per cycle, dates stay aligned. |
| R5 | Nightly at 04:00 Europe/Brussels, 3 attempts 10 minutes apart, then a mail; repeated every failed night. |
| R6 | Mail through `coverage-smtp` to `CERT_ALERT_EMAIL` (`info@mintplayer.com`, comma-separated list allowed). |
| R7 | The CodeCoverage deploy installs and updates the script and timer. |
| R8 | No browser certificate, no Studio in production; the operator certificate is server-only. |
| R9 | Local development stays unsecured, without certificates. |
| R10 | Migration right after the local rehearsal, on the owner's go; the cold rollback copy (4.4) is deleted after 4.8 passed; the `.ravendbdump` of 4.1 stays. |

## 7. Certificate validity and the licence

- **Measured (7.1.10, Developer licence):** the licence one-shot, exactly as the compose file runs it, failed (curl exit
  22, HTTP 500, *"The server certificate total duration is greater than 4 months. This is not allowed when using
  Developer license"*) while the server certificate was the 825-day one of §4.2, and activated (exit 0;
  `/license/status` Commercial, Type Developer, not expired) once the server ran a 90-day certificate.
- **Cited, not measured (no Community licence can be activated outside production):** in `Raven.Server` 7.1
  (decompiled, `ServerWide/SecretProtection.ValidateExpiration`, both overloads) the only certificate-duration rule is
  `if (licenseType == LicenseType.Developer && …)`: at most 4 months total and at most 4 months from now. There is no
  rule for Community or any other type. So production (Community) keeps the 2-year certificates of §4.2.
- ⚠️ Consequence for anyone testing locally with the Developer licence: issue the server certificate with `-days 90`.

## 8. The Docker spike (2026-10-11, Q-D56a)

Local and throwaway: the committed compose files copied with `web`, `seo-raven` and `seo-mail` renamed `spike-*` (the
owner's own `web` network was never joined), projects `spike-cc` and `spike-seo`; the apps, the fetcher and Chromium
replaced by `alpine` listeners on their ports (they need real secrets or are not published); RavenDB, the licence
one-shot and the relay ran their real images and settings. Certificates from §4.2's commands verbatim. Everything
(containers, volumes, networks, pulled images, certificates, the licence copy, backups) was removed afterwards.

**Measured:**
- §4.5–4.7 as written: RavenDB came up secured (`Server available on: https://coverage-raven:8080`, TCP 38888); the
  operator registered both app certificates and created both databases (201 each); `up -d` started the rest.
- The scoping, through Spark's `SparkRavenDbSecurity` in a Linux container (the CodeCoverage image's .NET runtime):
  plain `http://` got no answer, no client certificate got 403; `codecoverage` listed only `Coverage`, wrote, read,
  deployed an index, configured expiration and received a subscription batch over TLS on 38888; it was refused
  (`AuthorizationException`) reading or writing `SeoTester`, creating or deleting a database and registering a
  certificate, and got 403 listing certificates and activating a licence; `seotester` wrote its own database and was
  refused `Coverage`. §4.8's checks: unsecured `000`, app certificate on `SeoTester` 403, on `Coverage` 200.
- The README's *Backup* commands in their TLS form: stats and export, exit 0.
- The licence one-shot: §7.
- The container uids: §3.
- The network matrix (TCP connect from inside each container; ✔ = connected):

  | from \ to | raven 8080/38888 | smtp 587 | coverage-app | seotester-app | chromium | fetcher | internet |
  |---|---|---|---|---|---|---|---|
  | coverage-raven | (self) | – | ✔ | ✔ | – | – | – |
  | coverage-raven-license | ✔ | – | ✔ | – | – | – | – |
  | coverage-smtp | – | (self) | ✔ | ✔ | – | – | ✔ |
  | coverage-app | ✔ | ✔ | (self) | ✔ (`web`) | – | – | ✔ |
  | seotester-app | ✔ | ✔ | ✔ (`web`) | (self) | ✔ | ✔ | ✔ |
  | seo-chromium | – | – | – | ✔ | (self) | – | – |
  | seo-fetcher | – | – | – | ✔ | – | (self) | ✔ |

- The rollback (§4.9): after running secured, `down`, the cold copy of 4.4, then master's unsecured compose file on the
  **same volume**: healthy, the document written while secured read back over `http://`, the licence still there. Then
  the cold copy restored with 4.9's command and the secured file again: the app certificate read the same document
  (the registrations survived the unsecured interlude and the restore).

**Not measured:** file modes on a Linux host (Docker Desktop ignores host ownership on bind mounts); the Community
licence (§7, cited); the real app images against the secured server (master's image predates the certificate settings;
the same code ran in the probe instead).
