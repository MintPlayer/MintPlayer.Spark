# CodeCoverage's git sidecar, coverage-git (docs/coverage_commit_graph_PRD.md D1, D3, D24): the only container that runs
# git. No database client, no GitHub App key, no SPA, no Node: one bare, treeless mirror per repository on the
# codecoverage-git-data volume, and the commit-graph API on the internal coverage-git network. Built from the repository
# root (context .), like the app's image.
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
# git, kept patched through the base image (PRD §5.4); ca-certificates for https clones. Nothing else.
# The hooks directory every git process gets as core.hooksPath (D5): empty, owned by root and read-only, so neither git
# nor the non-root app user can ever place a hook in it. The mirror volume's mount point is created and handed to `app`
# because a fresh named volume copies the mount point's ownership from the image.
RUN apt-get update && \
    apt-get install -y --no-install-recommends git ca-certificates && \
    rm -rf /var/lib/apt/lists/* && \
    mkdir -p /opt/git-hooks-empty && chmod 0555 /opt/git-hooks-empty && \
    mkdir -p /data/git && chown app:app /data/git
# No system or global git config is ever read (D26); GitRunner sets these per process as well.
ENV GIT_CONFIG_NOSYSTEM=1 \
    GIT_CONFIG_GLOBAL=/dev/null \
    GIT_TERMINAL_PROMPT=0 \
    GitMirror__Root=/data/git \
    GitMirror__HooksPath=/opt/git-hooks-empty
USER app
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Selective csproj COPY for restore-layer caching: the sidecar's full transitive ProjectReference closure, which is just
# itself plus the shared git runner (it references no Spark, RavenDB or GitHub App project; SidecarIsolationTests).
COPY ["apps/CodeCoverage/CodeCoverage.Git/CodeCoverage.Git.csproj", "apps/CodeCoverage/CodeCoverage.Git/"]
COPY ["apps/CodeCoverage/CodeCoverage.GitMirror/CodeCoverage.GitMirror.csproj", "apps/CodeCoverage/CodeCoverage.GitMirror/"]
RUN dotnet restore "./apps/CodeCoverage/CodeCoverage.GitMirror/CodeCoverage.GitMirror.csproj"
COPY . .

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
WORKDIR "/src/apps/CodeCoverage/CodeCoverage.GitMirror"
RUN dotnet publish "./CodeCoverage.GitMirror.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "CodeCoverage.GitMirror.dll"]
