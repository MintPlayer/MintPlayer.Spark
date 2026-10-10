# RavenDB over TLS with client certificates — design and one-time migration of the live server

> ⚠️ **DRAFT until the Docker spike has run (SeoTester PRD §11, Q-D56a).** The settings, the permission model and every
> command shape below were **proven on 2026-10-10 against RavenDB 7.1.3** (the `RavenDB.Embedded` server, on Windows,
> unlicensed and then with the Developer licence; PRD §12 "D56"). They were **not** yet run against the pinned
> `ravendb/ravendb:7.1.10-ubuntu.22.04-x64` image, in Docker, on the Community licence: Docker Desktop was not running.
> Do not start §4 before §8 has been run and recorded.

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
| CA (`ca.crt`/`ca.key`, 10 years) | — (signs the others) | `ca.crt` beside each stack; **`ca.key` never** (owner's password manager, needed only to issue) | `ca.crt`: both apps and the licence one-shot |
| Server (`server.pfx`, 2 years, SAN `DNS:coverage-raven`, EKU server + client) | — | `/var/www/code-coverage/raven-certs/server.pfx` | `coverage-raven` only |
| Operator (`operator.crt`/`.key`) | **Cluster admin**, through `RAVEN_Security_WellKnownCertificates_Admin` (its thumbprint) | `/var/www/code-coverage/raven-operator/` | the licence one-shot only (and the owner's browser, as a `.p12`) |
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
| `/var/www/code-coverage/raven-certs/server.pfx` | RavenDB's container user (uid from `docker run --rm --entrypoint id ravendb/ravendb:7.1.10-ubuntu.22.04-x64`) | `0400` |
| `/var/www/code-coverage/raven-certs/ca.crt`, `codecoverage.crt` | root | `0644` |
| `/var/www/code-coverage/raven-certs/codecoverage.key` | the app's user (uid from `docker run --rm --entrypoint id ghcr.io/mintplayer/codecoverage:master`; .NET's `app` is 1654) | `0400` |
| `/var/www/code-coverage/raven-operator/operator.crt` | root | `0644` |
| `/var/www/code-coverage/raven-operator/operator.key` | curl's user (uid from `docker run --rm --entrypoint id curlimages/curl:8.11.1`) | `0400` |
| `/var/www/seotester/raven-certs/ca.crt`, `seotester.crt` | root | `0644` |
| `/var/www/seotester/raven-certs/seotester.key` | the SeoTester image's user (same check, `ghcr.io/mintplayer/seotester:master`) | `0400` |
| `/var/www/code-coverage/.env`: `RAVEN_SERVER_CERTIFICATE_PASSWORD`, `RAVEN_OPERATOR_THUMBPRINT` | (as today) | `0600` |

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

openssl req -x509 -newkey rsa:4096 -sha256 -days 3650 -nodes \
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

(`openssl req -x509 -CA` needs OpenSSL ≥ 3.0, which Ubuntu 22.04 and later have.) Then copy `ca.key` off the server
(password manager) and delete it here: `shred -u ca.key server.key`. Make the owner's browser certificate on the
owner's machine, never on the VPS: `openssl pkcs12 -export -inkey operator.key -in operator.crt -out operator.p12`.

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
shred -u /root/raven-ca/server.pfx.pass /root/raven-ca/*.key 2>/dev/null || true   # keys now live only where mounted
```

The directories must be traversable by the container users: `0750` root-owned works because Docker bind-mounts single
files by path (the daemon, not the container user, opens the directory). Keep `/root/raven-ca` only for the public
`.crt` files.

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

$OP "$R/admin/certificates" | grep -o '"Name":"[^"]*"'      # operator (well-known), codecoverage, seotester
$OP "$R/databases?namesOnly=true"                           # Coverage, SeoTester
```

Each `PUT` answers 201 (re-registering the same certificate also answered 201 in the spike).

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

Then open https://coverage.mintplayer.com, sign in, open a repository.

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
- **Studio:** the operator `.p12` in the owner's browser; RavenDB publishes no port, so tunnel to the container's
  address on `code-coverage_coverage-db` and browse `https://coverage-raven:<local port>` with `coverage-raven` mapped to
  `127.0.0.1` in the local hosts file (the certificate's name).
- **Licence:** replace `raven-license.json`, then `docker compose --env-file .env up coverage-raven-license`.

## 6. Renewal (calendar: 2 years minus a month after 4.2)

Issue a new certificate with the CA (the 4.2 command for that name), register the new app certificate **beside** the
old one (a second `register` with a new `Name`), swap the files, restart the app, then delete the old registration
(`$OP -X DELETE "$R/admin/certificates?thumbprint=<old>"`). The server certificate: replace `server.pfx` (same
password or update `.env`) and restart `coverage-raven`; clients trust the CA, not the leaf. The operator: new
thumbprint in `.env`, restart `coverage-raven`.

## 7. What the Developer licence would refuse

The spike found that the **Developer** licence refuses activation while the server certificate is valid for more than
4 months (*"The server certificate total duration is greater than 4 months. This is not allowed when using Developer
license"*). Production runs the **Community** licence; whether Community has a limit of its own on certificates is
checked in §8.

## 8. The spike to run before §4 (Q-D56a)

In Docker, with the pinned image, on a throwaway container and volume, certificates from 4.2 (SAN plus
`DNS:localhost`), the compose file's RAVEN_* settings: (1) unsecured `http://` and a client without a certificate are
refused; (2) the operator creates two databases and registers two app certificates; (3) an app certificate lists only
its own database, reads, writes, deploys an index, configures expiration and receives a subscription batch (TCP 38888)
through Spark's `SparkRavenDbSecurity`; and is refused reading or writing the other database, creating or deleting a
database, listing or registering certificates, activating a licence; (4) the operator exports a database; (5) the
licence one-shot's exact curl command activates the **Community** licence over TLS; (6) the three `id -u` values of
§4.3; (7) an unsecured restart on the same volume (the rollback of 4.9) serves the data. Remove the container, volume
and certificates afterwards; record the results in PRD §12 and remove the DRAFT banner.
