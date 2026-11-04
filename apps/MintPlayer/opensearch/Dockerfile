# The search engine of mintplayer.com (docs/spark_search_PRD.md D13): OpenSearch 3.9.0 with the ICU analysis plugin,
# built and pushed by .github/workflows/mintplayer-deploy.yml as ghcr.io/mintplayer/mintplayer-opensearch:master.
# Configuration (single node, security plugin off, heap, no mmap) is in apps/MintPlayer/docker-compose.yml: the engine
# is reachable only on the internal network mintplayer-search, by mintplayer-app alone.
FROM opensearchproject/opensearch:3.9.0

# Installed at build time: the running container has no route out (mintplayer-search is internal).
RUN /usr/share/opensearch/bin/opensearch-plugin install --batch analysis-icu
