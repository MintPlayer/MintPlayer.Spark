#!/usr/bin/env bash
# Starts a single-node Garage (S3-compatible) for the S3 file store's conformance tests (#700 M5) and prints the
# SPARK_S3_* variables they read, as KEY=value lines (CI appends them to $GITHUB_ENV). The RPC secret and the access key
# are generated per run. Pinned to the version spike SP9 ran.
set -euo pipefail

name="${GARAGE_CONTAINER:-garage}"
here="$(cd "$(dirname "$0")" && pwd)"
toml="$here/garage.toml"
if command -v cygpath > /dev/null; then toml="$(cygpath -w "$toml")"; fi

MSYS_NO_PATHCONV=1 docker run -d --name "$name" -p 3900:3900 \
  -e GARAGE_RPC_SECRET="$(openssl rand -hex 32)" \
  -v "$toml:/etc/garage.toml:ro" \
  dxflrs/garage:v1.1.0 > /dev/null

garage() { MSYS_NO_PATHCONV=1 docker exec "$name" /garage "$@"; }

for _ in $(seq 1 30); do garage status > /dev/null 2>&1 && break; sleep 1; done
node="$(garage node id -q 2> /dev/null | cut -d@ -f1)"
garage layout assign -z dc1 -c 1G "$node" > /dev/null 2>&1
garage layout apply --version 1 > /dev/null 2>&1
garage bucket create spark-files > /dev/null 2>&1

key_id="GK$(openssl rand -hex 12)"
key_secret="$(openssl rand -hex 32)"
garage key import --yes -n spark "$key_id" "$key_secret" > /dev/null 2>&1
garage bucket allow --read --write --owner spark-files --key spark > /dev/null 2>&1

echo "SPARK_S3_URL=http://localhost:3900"
echo "SPARK_S3_REGION=garage"
echo "SPARK_S3_BUCKET=spark-files"
echo "SPARK_S3_ACCESS_KEY=$key_id"
echo "SPARK_S3_SECRET_KEY=$key_secret"
