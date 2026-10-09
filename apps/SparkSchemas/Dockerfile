# schemas.spark.mintplayer.com: the published Spark JSON schemas (#264, G-Q9, G-Q19, G-Q21).
#
# The image holds no schema of its own. Its build stage downloads the assets of EVERY schemas/v{n}
# GitHub release into /v{n}/, so the site can be rebuilt from the releases alone and every revision
# an application's "$schema" may name stays online. The publish workflow (dotnet-build-master.yml)
# creates those releases; spark-schemas-deploy.yml rebuilds this image whenever it does.
#
# Build context: apps/SparkSchemas (nothing else of the repository is needed).
#   docker build --secret id=github_token,env=GH_TOKEN --build-arg SCHEMAS_REFRESH=$(date +%s) apps/SparkSchemas

FROM alpine:3.24 AS fetch
RUN apk add --no-cache curl jq
ARG GITHUB_REPOSITORY=MintPlayer/MintPlayer.Spark
COPY fetch-releases.sh /usr/local/bin/fetch-releases.sh
# CRs stripped: a Windows checkout (core.autocrlf) would otherwise hand sh a "set -eu\r".
RUN sed -i 's/\r$//' /usr/local/bin/fetch-releases.sh && chmod +x /usr/local/bin/fetch-releases.sh
# ⚠️ The download below has the same command text on every build, so a layer cache would replay an
# old set of releases forever. Changing this argument (the deploy passes the run id) invalidates it.
ARG SCHEMAS_REFRESH=0
RUN --mount=type=secret,id=github_token,required=false \
    echo "refresh: ${SCHEMAS_REFRESH}" && fetch-releases.sh "${GITHUB_REPOSITORY}" /site

FROM nginx:1.31-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=fetch /site /usr/share/nginx/html
EXPOSE 80
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
    CMD wget -qO- http://127.0.0.1/health >/dev/null || exit 1
