#!/usr/bin/env bash
# Renews the RavenDB certificates of coverage-raven before they expire (docs/code-coverage/raven-tls-migration.md §6).
#
# Installed by .github/workflows/code-coverage-deploy.yml as /usr/local/sbin/raven-cert-renew and run nightly by
# raven-cert-renew.timer. Decisions R1-R10 of the runbook:
#   - the private CA (CA_DIR/ca.crt + ca.key, root-only) signs every leaf: the server, the operator, codecoverage,
#     seotester and mintplayer certificates;
#   - when ANY leaf has RENEW_BEFORE_DAYS or fewer left, ALL of them are re-issued together with NEW keys, valid DAYS;
#   - the new app certificates are registered beside the old ones with the still-valid operator BEFORE anything is
#     swapped, so no app is ever without a registered certificate; then the files are swapped, RavenDB is recreated
#     (new server certificate, new operator thumbprint), the apps are recreated, and the old registrations are removed;
#   - every RavenDB call is a throwaway curl container on the INTERNAL network coverage-db: RavenDB gets no new network,
#     no port, nothing reaches it from outside;
#   - --scheduled: up to ATTEMPTS tries ATTEMPT_DELAY apart; when all fail, a mail to CERT_ALERT_EMAIL through
#     coverage-smtp (on the internal network coverage-mail). Run nightly, so a lasting failure mails every night.
#
# Usage: raven-cert-renew [--status | --force | --scheduled]
#   (none)       renew when due, one attempt, no mail
#   --status     print each certificate's expiry and whether a renewal is due; changes nothing
#   --force      renew now, whatever the dates (rehearsal, or after a suspected key leak)
#   --scheduled  what the timer runs: renew when due, with retries and the failure mail
#
# Nothing secret is ever printed: keys stay in files, the pfx password is read from .env into an environment variable
# that only openssl sees, and the operator thumbprint is written into .env without being echoed.

set -euo pipefail
# Every file this script creates (keys, the staged .env copy, the mail) is private to root; final modes are explicit.
umask 077

CC_DIR=${CC_DIR:-/var/www/code-coverage}
SEO_DIR=${SEO_DIR:-/var/www/seotester}
MP_DIR=${MP_DIR:-/var/www/mintplayer}
CA_DIR=${CA_DIR:-/root/raven-ca}
DAYS=${RAVEN_CERT_DAYS:-825}
RENEW_BEFORE_DAYS=${RAVEN_RENEW_BEFORE_DAYS:-30}
CA_WARN_DAYS=${RAVEN_CA_WARN_DAYS:-365}
ATTEMPTS=${RAVEN_RENEW_ATTEMPTS:-3}
ATTEMPT_DELAY=${RAVEN_RENEW_ATTEMPT_DELAY:-600}
CURL_IMAGE=curlimages/curl:8.11.1
LOCK=${RAVEN_RENEW_LOCK:-/run/raven-cert-renew.lock}

CC_PROJECT=${CC_PROJECT:-$(basename "$CC_DIR")}
DB_NET="${CC_PROJECT}_coverage-db"
MAIL_NET="${CC_PROJECT}_coverage-mail"
R=https://coverage-raven:8080

log() { printf '%s raven-cert-renew: %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2; }
die() { log "ERROR: $*"; exit 1; }

# Inputs are validated before they reach sed, a URL, .env or a mail header: a malformed value is refused, never used.
is_thumbprint() { [[ $1 =~ ^[0-9A-F]{40}$ ]]; }
is_email() { [[ $1 =~ ^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$ ]]; }
is_domain() { [[ $1 =~ ^[A-Za-z0-9.-]+\.[A-Za-z]{2,}$ ]]; }
for n in "$DAYS" "$RENEW_BEFORE_DAYS" "$CA_WARN_DAYS" "$ATTEMPTS" "$ATTEMPT_DELAY"; do
  [[ $n =~ ^[0-9]+$ ]] || die "a numeric setting is not a number"
done

# A value from CodeCoverage's .env, never printed by this script.
envval() { sed -n "s/^$1=//p" "$CC_DIR/.env" | tail -n 1; }

has_seo() { [ -f "$SEO_DIR/raven-certs/seotester.crt" ]; }
has_mp() { [ -f "$MP_DIR/raven-certs/mintplayer.crt" ]; }

cc() { docker compose --project-directory "$CC_DIR" --env-file "$CC_DIR/.env" "$@"; }
seo() { docker compose --project-directory "$SEO_DIR" --env-file "$SEO_DIR/.env" "$@"; }
mp() { docker compose --project-directory "$MP_DIR" --env-file "$MP_DIR/.env" "$@"; }

# The server certificate (public part) out of server.pfx; the password goes to openssl through the environment only.
server_crt() {
  PFXPASS=$(envval RAVEN_SERVER_CERTIFICATE_PASSWORD) \
    openssl pkcs12 -in "$CC_DIR/raven-certs/server.pfx" -nokeys -clcerts -passin env:PFXPASS 2>/dev/null \
    | openssl x509
}

thumbprint() {
  local t; t=$(openssl x509 -in "$1" -noout -fingerprint -sha1 | sed 's/.*=//; s/://g')
  is_thumbprint "$t" || die "no valid thumbprint for $1"
  printf '%s' "$t"
}
not_after() { openssl x509 -noout -enddate | sed 's/notAfter=//'; }

# Exit 0 when the certificate on stdin expires within $1 days.
expires_within() { ! openssl x509 -noout -checkend $(( $1 * 86400 )) > /dev/null; }

leaves() {   # name and a command that prints the certificate, one per line
  echo "server server_crt"
  echo "operator cat $CC_DIR/raven-operator/operator.crt"
  echo "codecoverage cat $CC_DIR/raven-certs/codecoverage.crt"
  if has_seo; then echo "seotester cat $SEO_DIR/raven-certs/seotester.crt"; fi
  if has_mp; then echo "mintplayer cat $MP_DIR/raven-certs/mintplayer.crt"; fi
}

renewal_due() {
  local name cmd
  while read -r name cmd; do
    if $cmd | expires_within "$RENEW_BEFORE_DAYS"; then log "$name expires within $RENEW_BEFORE_DAYS days"; return 0; fi
  done < <(leaves)
  return 1
}

status() {
  local name cmd
  printf '%-13s %s\n' "ca" "$(not_after < "$CA_DIR/ca.crt")"
  while read -r name cmd; do printf '%-13s %s\n' "$name" "$($cmd | not_after)"; done < <(leaves)
  if renewal_due; then echo "renewal: due"; else echo "renewal: not due (renews at $RENEW_BEFORE_DAYS days left)"; fi
}

# A cluster-admin call to RavenDB with the given operator certificate files, from a throwaway container on coverage-db.
op() {   # crt key curl-args...
  local crt=$1 key=$2; shift 2
  # As curl's own user (100), which owns operator.key (runbook §3): no root in the container. The CA is the public
  # copy beside the stack (0644); CA_DIR stays root-only and is never mounted.
  docker run --rm --network "$DB_NET" \
    -v "$crt:/op.crt:ro" -v "$key:/op.key:ro" -v "$CC_DIR/raven-certs/ca.crt:/ca.crt:ro" \
    "$CURL_IMAGE" -sS -f --retry 5 --retry-delay 3 --retry-connrefused --cert /op.crt --key /op.key --cacert /ca.crt "$@"
}

register() {   # operator-crt operator-key name database certificate-file
  local b64; b64=$(openssl x509 -in "$5" -outform der | base64 -w0)
  op "$1" "$2" -o /dev/null -X PUT "$R/admin/certificates" -H 'Content-Type: application/json' \
    --data "{\"Name\":\"$3\",\"Certificate\":\"$b64\",\"SecurityClearance\":\"ValidUser\",\"Permissions\":{\"$4\":\"Admin\"}}"
}

issue() {   # dir name [server]
  local dir=$1 name=$2 eku=clientAuth san=()
  if [ "${3:-}" = server ]; then eku=serverAuth,clientAuth; san=(-addext "subjectAltName=DNS:coverage-raven"); fi
  openssl req -x509 -newkey rsa:2048 -sha256 -days "$DAYS" -nodes \
    -CA "$CA_DIR/ca.crt" -CAkey "$CA_DIR/ca.key" -keyout "$dir/$name.key" -out "$dir/$name.crt" -subj "/CN=$name" \
    "${san[@]}" -addext "basicConstraints=critical,CA:FALSE" -addext "keyUsage=critical,digitalSignature,keyEncipherment" \
    -addext "extendedKeyUsage=$eku" 2>/dev/null
}

# Replaces a file, keeping its owner and mode (the container users of runbook §3).
replace() {   # new-file target
  install -m "$(stat -c %a "$2")" -o "$(stat -c %u "$2")" -g "$(stat -c %g "$2")" "$1" "$2"
}

wait_healthy() {   # compose-function service seconds
  local id i
  id=$($1 ps -q "$2")
  for (( i = 0; i < $3; i += 5 )); do
    [ "$(docker inspect -f '{{.State.Health.Status}}' "$id" 2>/dev/null)" = healthy ] && return 0
    sleep 5
  done
  return 1
}

http_ok() {   # compose-function service path seconds: 200 from inside the container, as the deploys check it
  local i code
  for (( i = 0; i < $4; i += 10 )); do
    code=$($1 exec -T "$2" bash -c "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET $3 HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3 && head -1 <&3" 2>/dev/null | grep -o '[0-9][0-9][0-9]' | head -1 || true)
    [ "$code" = 200 ] && return 0
    sleep 10
  done
  return 1
}

# Removes every app registration (Name codecoverage* / seotester* / mintplayer*) that is not one of the installed
# certificates. Run after every renewal and harmless when there is nothing to remove, so an interrupted cleanup finishes
# the next night.
cleanup_registrations() {
  local keep expected=1 old
  keep="$(thumbprint "$CC_DIR/raven-certs/codecoverage.crt")"
  if has_seo; then keep="$keep $(thumbprint "$SEO_DIR/raven-certs/seotester.crt")"; expected=$((expected + 1)); fi
  if has_mp; then keep="$keep $(thumbprint "$MP_DIR/raven-certs/mintplayer.crt")"; expected=$((expected + 1)); fi
  # Never delete with an incomplete keep-list: that would remove the registrations in use.
  [ "$(wc -w <<< "$keep")" -eq "$expected" ] || die "could not read the installed thumbprints; nothing removed"
  op "$CC_DIR/raven-operator/operator.crt" "$CC_DIR/raven-operator/operator.key" "$R/admin/certificates" \
    | KEEP="$keep" python3 -c '
import json, os, sys
keep = set(os.environ["KEEP"].upper().split())
def walk(node):
    if isinstance(node, dict):
        if "Thumbprint" in node and "Name" in node:
            yield node
        for value in node.values():
            yield from walk(value)
    elif isinstance(node, list):
        for value in node:
            yield from walk(value)
for cert in walk(json.load(sys.stdin)):
    name, thumb = cert["Name"] or "", (cert["Thumbprint"] or "").upper()
    if name.startswith(("codecoverage", "seotester", "mintplayer")) and thumb not in keep:
        print(thumb)
' | sort -u | while read -r old; do
      is_thumbprint "$old" || { log "skipping a malformed thumbprint from the server"; continue; }
      log "removing the old registration $old"
      op "$CC_DIR/raven-operator/operator.crt" "$CC_DIR/raven-operator/operator.key" -o /dev/null -X DELETE "$R/admin/certificates?thumbprint=$old"
    done
}

renew() {
  [ -f "$CA_DIR/ca.key" ] || die "$CA_DIR/ca.key is missing: the CA must stay on the server (runbook R1)"
  [ -n "$(envval RAVEN_SERVER_CERTIFICATE_PASSWORD)" ] || die "RAVEN_SERVER_CERTIFICATE_PASSWORD is not set in $CC_DIR/.env"
  wait_healthy cc coverage-raven 60 || die "coverage-raven is not healthy; nothing changed"

  local stamp new old
  stamp=$(date -u +%Y%m%dT%H%M%SZ)
  STAGE="$CA_DIR/renew-$stamp"   # global: the EXIT trap runs after this function's locals are gone
  new="$STAGE/new"; old="$STAGE/old"
  mkdir -p "$new" "$old"
  # Keys of this run are shredded whatever happens (EXIT: die exits the process); the old ones are kept only until the
  # swap is verified.
  trap 'shred -u "$STAGE"/*/*.key "$STAGE"/*/*.pfx "$STAGE"/old/.env 2>/dev/null || true; rm -rf "$STAGE"' EXIT

  log "issuing new certificates ($DAYS days)"
  issue "$new" server server
  issue "$new" operator
  issue "$new" codecoverage
  if has_seo; then issue "$new" seotester; fi
  if has_mp; then issue "$new" mintplayer; fi
  openssl verify -CAfile "$CA_DIR/ca.crt" "$new"/*.crt > /dev/null || die "the new certificates do not verify against the CA"
  PFXPASS=$(envval RAVEN_SERVER_CERTIFICATE_PASSWORD) \
    openssl pkcs12 -export -inkey "$new/server.key" -in "$new/server.crt" -certfile "$CA_DIR/ca.crt" \
      -out "$new/server.pfx" -passout env:PFXPASS

  # 1. The new app certificates are registered while the old operator still administers the server.
  log "registering the new app certificates beside the old ones"
  register "$CC_DIR/raven-operator/operator.crt" "$CC_DIR/raven-operator/operator.key" "codecoverage-$stamp" Coverage "$new/codecoverage.crt"
  if has_seo; then
    register "$CC_DIR/raven-operator/operator.crt" "$CC_DIR/raven-operator/operator.key" "seotester-$stamp" SeoTester "$new/seotester.crt"
  fi
  if has_mp; then
    register "$CC_DIR/raven-operator/operator.crt" "$CC_DIR/raven-operator/operator.key" "mintplayer-$stamp" MintPlayer "$new/mintplayer.crt"
  fi

  # 2. Swap the server and operator files and the thumbprint; keep the old ones to roll back.
  cp -p "$CC_DIR/raven-certs/server.pfx" "$CC_DIR/raven-operator/operator.crt" "$CC_DIR/raven-operator/operator.key" "$CC_DIR/.env" "$old/"
  replace "$new/server.pfx" "$CC_DIR/raven-certs/server.pfx"
  replace "$new/operator.crt" "$CC_DIR/raven-operator/operator.crt"
  replace "$new/operator.key" "$CC_DIR/raven-operator/operator.key"
  local op_thumb; op_thumb=$(thumbprint "$new/operator.crt")   # validated: 40 hex characters
  sed -i "s/^RAVEN_OPERATOR_THUMBPRINT=.*/RAVEN_OPERATOR_THUMBPRINT=$op_thumb/" "$CC_DIR/.env"

  # 3. RavenDB reads its certificate and the well-known admin at startup: recreate it (a bind-mounted file is pinned to
  #    its old inode until the container is recreated). Roll back if it does not come up or the new operator is refused.
  log "recreating coverage-raven with the new server and operator certificates"
  if ! { cc up -d --force-recreate --no-deps coverage-raven && wait_healthy cc coverage-raven 300 \
         && op "$CC_DIR/raven-operator/operator.crt" "$CC_DIR/raven-operator/operator.key" -o /dev/null "$R/admin/certificates"; }; then
    log "coverage-raven did not accept the new certificates: rolling back"
    replace "$old/server.pfx" "$CC_DIR/raven-certs/server.pfx"
    replace "$old/operator.crt" "$CC_DIR/raven-operator/operator.crt"
    replace "$old/operator.key" "$CC_DIR/raven-operator/operator.key"
    cp -p "$old/.env" "$CC_DIR/.env"
    cc up -d --force-recreate --no-deps coverage-raven && wait_healthy cc coverage-raven 300 || log "ROLLBACK FAILED: coverage-raven is not healthy"
    die "renewal rolled back; the old certificates are in use"
  fi

  # 4. The apps: swap their files and recreate them; their new certificates are already registered.
  log "recreating coverage-app with its new certificate"
  replace "$new/codecoverage.crt" "$CC_DIR/raven-certs/codecoverage.crt"
  replace "$new/codecoverage.key" "$CC_DIR/raven-certs/codecoverage.key"
  cc up -d --force-recreate --no-deps coverage-app
  http_ok cc coverage-app /health/ready 660 || die "coverage-app is not ready with its new certificate"

  if has_seo; then
    log "recreating seotester-app with its new certificate"
    replace "$new/seotester.crt" "$SEO_DIR/raven-certs/seotester.crt"
    replace "$new/seotester.key" "$SEO_DIR/raven-certs/seotester.key"
    if [ -n "$(seo ps -q seotester-app)" ]; then
      seo up -d --force-recreate --no-deps seotester-app
      http_ok seo seotester-app /health 360 || die "seotester-app is not ready with its new certificate"
    fi
  fi

  if has_mp; then
    log "recreating mintplayer-app with its new certificate"
    replace "$new/mintplayer.crt" "$MP_DIR/raven-certs/mintplayer.crt"
    replace "$new/mintplayer.key" "$MP_DIR/raven-certs/mintplayer.key"
    if [ -n "$(mp ps -q mintplayer-app)" ]; then
      mp up -d --force-recreate --no-deps mintplayer-app
      http_ok mp mintplayer-app /health 360 || die "mintplayer-app is not ready with its new certificate"
    fi
  fi

  # 5. Only now are the old app certificates unused.
  cleanup_registrations
  log "renewal complete"
  status
}

mail_alert() {   # subject body
  local to domain from msg a list rcpt=() valid=()
  to=$(envval CERT_ALERT_EMAIL)
  [ -n "$to" ] || { log "CERT_ALERT_EMAIL is not set in $CC_DIR/.env: no mail sent"; return 1; }
  domain=$(envval MAIL_SENDER_DOMAIN); is_domain "$domain" || domain=coverage.mintplayer.com
  from="no-reply@$domain"
  # Each address must be a plain address (no CR/LF, no header syntax); anything else is dropped, never sent.
  IFS=',' read -ra list <<< "$to"
  for a in "${list[@]}"; do
    a=${a//[[:space:]]/}
    if is_email "$a"; then rcpt+=(--mail-rcpt "$a"); valid+=("$a"); elif [ -n "$a" ]; then log "CERT_ALERT_EMAIL holds an invalid address; skipped"; fi
  done
  [ "${#valid[@]}" -gt 0 ] || { log "no valid address in CERT_ALERT_EMAIL: no mail sent"; return 1; }
  # In CA_DIR (0700), not /tmp: the docker daemon must see the file, and nothing else should.
  msg=$(mktemp "$CA_DIR/mail.XXXXXX")
  {
    printf 'From: RavenDB certificates <%s>\r\nTo: %s\r\nSubject: %s\r\nDate: %s\r\n' "$from" "$(IFS=,; echo "${valid[*]}")" "$1" "$(date -R)"
    printf 'Content-Type: text/plain; charset=utf-8\r\n\r\n%s\r\n' "$2"
  } > "$msg"
  chmod 0644 "$msg"   # readable by curl's user in the container; the directory stays 0700
  docker run --rm --network "$MAIL_NET" -v "$msg:/m.txt:ro" "$CURL_IMAGE" -sS \
    --url smtp://coverage-smtp:587 --mail-from "$from" "${rcpt[@]}" -T /m.txt
  local rc=$?
  rm -f "$msg"
  return $rc
}

scheduled() {
  local attempt output ca_note=""
  if expires_within "$CA_WARN_DAYS" < "$CA_DIR/ca.crt"; then
    ca_note="The CA itself ($CA_DIR/ca.crt) expires on $(not_after < "$CA_DIR/ca.crt"): issue a new CA and re-issue everything (runbook §6)."
    mail_alert "RavenDB CA expires within $CA_WARN_DAYS days" "$ca_note" || true
  fi
  renewal_due || { log "nothing due"; return 0; }
  for (( attempt = 1; attempt <= ATTEMPTS; attempt++ )); do
    log "attempt $attempt of $ATTEMPTS"
    # Each attempt is its own process: bash disables set -e inside a function called from an if condition, so
    # `if output=$(renew)` would carry on past a failed step. The child skips the lock this process holds.
    if output=$(RAVEN_RENEW_CHILD=1 "$BASH" "$0" --force 2>&1); then printf '%s\n' "$output" >&2; return 0; fi
    printf '%s\n' "$output" >&2
    (( attempt < ATTEMPTS )) && sleep "$ATTEMPT_DELAY"
  done
  mail_alert "RavenDB certificate renewal FAILED on $(hostname)" \
"The nightly renewal of the RavenDB certificates failed $ATTEMPTS times. It runs again tomorrow night.

Status:
$(status 2>&1)

Last attempt:
$(printf '%s\n' "$output" | tail -n 40)

Logs: journalctl -u raven-cert-renew. Runbook: docs/code-coverage/raven-tls-migration.md §6." || true
  return 1
}

if [ -z "${RAVEN_RENEW_CHILD:-}" ]; then
  exec 9> "$LOCK"
  flock -n 9 || die "another renewal is running"
fi

case "${1:-}" in
  --status) status ;;
  --force) renew ;;
  --scheduled) scheduled ;;
  "") if renewal_due; then renew; else log "nothing due"; status; fi ;;
  *) die "unknown option $1 (use --status, --force or --scheduled)" ;;
esac
