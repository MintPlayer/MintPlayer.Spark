#!/usr/bin/env bash
# Scans each image given (local or pullable) with Trivy and fails when any carries an openssh-server package or
# CVE-2026-123158 (#700 PRD C1, §0 item 14; the check itself is tools/ci/trivy-no-sshd.mjs). Every image is scanned before
# the exit, so one run names every offender.
#
#   tools/ci/trivy-no-sshd.sh <image>...
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
reports="${RUNNER_TEMP:-/tmp}/trivy"
mkdir -p "$reports" "$reports/cache"

status=0
for image in "$@"; do
  report="$reports/$(echo "$image" | tr '/:' '__').json"
  # The docker socket, so a locally built (loaded, never pushed) image is scanned as it is.
  docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v "$reports/cache:/root/.cache/" aquasec/trivy:0.58.1 \
    image --quiet --format json --list-all-pkgs --scanners vuln "$image" > "$report"
  node "$here/trivy-no-sshd.mjs" "$image" "$report" || status=1
done
exit $status
