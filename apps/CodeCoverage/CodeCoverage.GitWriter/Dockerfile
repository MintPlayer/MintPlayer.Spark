# The merge queue's git writer, coverage-git-writer (docs/coverage_merge_queue_PRD.md D2, M3): the only container that
# pushes on the queue's behalf. No database client, no GitHub App key, no SPA, no Node: a per-job bare, blob-less clone on
# the codecoverage-git-writer-jobs volume, deleted when the job ends, and the jobs API on the internal coverage-git-writer
# network. Built from the repository root (context .), like the other images.
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
# git from the distribution (Debian, kept patched through the base image) and ca-certificates for https. Nothing else.
# ⚠️ The git floor (§4.3, T8): 2.50.1, or a release carrying the July 2025 security batch (CVE-2025-48384, -48385, …).
# Upstream shipped that batch as 2.43.7, 2.44.4, 2.45.4, 2.46.4, 2.47.3, 2.48.2, 2.49.1 and 2.50.1; Debian also backports
# it into older version numbers and names the CVE in the package changelog. The check below accepts exactly those and
# FAILS THE BUILD otherwise (CI's image check builds this file on every pull request), so a base image whose git is older
# and unpatched can never be published. If it fails: install git from the distribution's backports, or build 2.50.1+.
# The hooks directory every git process gets as core.hooksPath (D5): empty, owned by root, read-only. The jobs volume's
# mount point is created and handed to `app`, because a fresh named volume copies the mount point's ownership.
RUN apt-get update && \
    apt-get install -y --no-install-recommends git ca-certificates && \
    rm -rf /var/lib/apt/lists/* && \
    v="$(git --version | sed -n 's/^git version \([0-9]*\)\.\([0-9]*\)\.\([0-9]*\).*/\1 \2 \3/p')" && \
    set -- $v && echo "git $1.$2.$3" && \
    { [ "$1" -gt 2 ] || \
      { [ "$1" -eq 2 ] && [ "$2" -gt 50 ]; } || \
      { [ "$1" -eq 2 ] && [ "$2" -eq 50 ] && [ "$3" -ge 1 ]; } || \
      { [ "$1" -eq 2 ] && [ "$2" -eq 49 ] && [ "$3" -ge 1 ]; } || \
      { [ "$1" -eq 2 ] && [ "$2" -eq 48 ] && [ "$3" -ge 2 ]; } || \
      { [ "$1" -eq 2 ] && [ "$2" -eq 47 ] && [ "$3" -ge 3 ]; } || \
      { [ "$1" -eq 2 ] && [ "$2" -eq 46 ] && [ "$3" -ge 4 ]; } || \
      { [ "$1" -eq 2 ] && [ "$2" -eq 45 ] && [ "$3" -ge 4 ]; } || \
      { [ "$1" -eq 2 ] && [ "$2" -eq 44 ] && [ "$3" -ge 4 ]; } || \
      { [ "$1" -eq 2 ] && [ "$2" -eq 43 ] && [ "$3" -ge 7 ]; } || \
      zcat /usr/share/doc/git/changelog.Debian.gz 2>/dev/null | grep -q 'CVE-2025-48384'; } || \
    { echo "git $1.$2.$3 lacks the July 2025 security fixes (CVE-2025-48384); see the comment above" >&2; exit 1; } && \
    mkdir -p /opt/git-hooks-empty && chmod 0555 /opt/git-hooks-empty && \
    mkdir -p /data/jobs && chown app:app /data/jobs
# No system or global git config is ever read; GitRunner sets these per process as well.
ENV GIT_CONFIG_NOSYSTEM=1 \
    GIT_CONFIG_GLOBAL=/dev/null \
    GIT_TERMINAL_PROMPT=0 \
    GitWriter__Root=/data/jobs \
    GitWriter__HooksPath=/opt/git-hooks-empty
USER app
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Selective csproj COPY for restore-layer caching: the writer's full transitive ProjectReference closure, which is just
# itself plus the shared git library and the versioning engine (no Spark, RavenDB or GitHub App project; the isolation test in its tests).
COPY ["apps/CodeCoverage/CodeCoverage.Git/CodeCoverage.Git.csproj", "apps/CodeCoverage/CodeCoverage.Git/"]
COPY ["apps/CodeCoverage/CodeCoverage.Versioning/CodeCoverage.Versioning.csproj", "apps/CodeCoverage/CodeCoverage.Versioning/"]
COPY ["apps/CodeCoverage/CodeCoverage.GitWriter/CodeCoverage.GitWriter.csproj", "apps/CodeCoverage/CodeCoverage.GitWriter/"]
RUN dotnet restore "./apps/CodeCoverage/CodeCoverage.GitWriter/CodeCoverage.GitWriter.csproj"
COPY . .

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
WORKDIR "/src/apps/CodeCoverage/CodeCoverage.GitWriter"
RUN dotnet publish "./CodeCoverage.GitWriter.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "CodeCoverage.GitWriter.dll"]
