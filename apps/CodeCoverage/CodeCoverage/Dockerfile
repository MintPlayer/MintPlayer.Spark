FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER app
WORKDIR /app
# 8080 only, deliberately. Traefik's port detection picks the single exposed port; exposing 8081 as
# well makes it ambiguous and the router can end up pointing at the wrong one.
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Node.js for the Angular build
RUN apt-get update && \
    apt-get -y install curl && \
    curl -sL https://deb.nodesource.com/setup_22.x | bash && \
    apt-get -y install nodejs && \
    rm -rf /var/lib/apt/lists/*

ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Selective csproj COPY for restore-layer caching. This must be CodeCoverage's full transitive
# ProjectReference closure, because `dotnet restore` below runs before `COPY . .` -- a missing entry
# fails the restore inside the image rather than at review time.
COPY ["apps/CodeCoverage/CodeCoverage/CodeCoverage.csproj", "apps/CodeCoverage/CodeCoverage/"]
COPY ["apps/CodeCoverage/CodeCoverage.Library/CodeCoverage.Library.csproj", "apps/CodeCoverage/CodeCoverage.Library/"]
COPY ["libs/spark/MintPlayer.Spark/MintPlayer.Spark.csproj", "libs/spark/MintPlayer.Spark/"]
COPY ["libs/spark/MintPlayer.Spark.Abstractions/MintPlayer.Spark.Abstractions.csproj", "libs/spark/MintPlayer.Spark.Abstractions/"]
COPY ["libs/authorization/MintPlayer.Spark.Authorization/MintPlayer.Spark.Authorization.csproj", "libs/authorization/MintPlayer.Spark.Authorization/"]
COPY ["libs/controllers/MintPlayer.Spark.Controllers/MintPlayer.Spark.Controllers.csproj", "libs/controllers/MintPlayer.Spark.Controllers/"]
COPY ["libs/cron/MintPlayer.Spark.Cron/MintPlayer.Spark.Cron.csproj", "libs/cron/MintPlayer.Spark.Cron/"]
COPY ["libs/messaging/MintPlayer.Spark.Messaging/MintPlayer.Spark.Messaging.csproj", "libs/messaging/MintPlayer.Spark.Messaging/"]
COPY ["libs/messaging/MintPlayer.Spark.Messaging.Abstractions/MintPlayer.Spark.Messaging.Abstractions.csproj", "libs/messaging/MintPlayer.Spark.Messaging.Abstractions/"]
COPY ["libs/migrations/MintPlayer.Spark.Migrations/MintPlayer.Spark.Migrations.csproj", "libs/migrations/MintPlayer.Spark.Migrations/"]
COPY ["libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/MintPlayer.Spark.Webhooks.GitHub.csproj", "libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/"]
COPY ["libs/webhooks/MintPlayer.Spark.Webhooks.GitHub.DevTunnel/MintPlayer.Spark.Webhooks.GitHub.DevTunnel.csproj", "libs/webhooks/MintPlayer.Spark.Webhooks.GitHub.DevTunnel/"]
COPY ["libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/MintPlayer.Spark.SubscriptionWorker.Abstractions.csproj", "libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/"]
COPY ["libs/source_generators/MintPlayer.Spark.SourceGenerators/MintPlayer.Spark.SourceGenerators.csproj", "libs/source_generators/MintPlayer.Spark.SourceGenerators/"]
COPY ["libs/socket_extensions/MintPlayer.Dotnet.SocketExtensions/MintPlayer.Dotnet.SocketExtensions.csproj", "libs/socket_extensions/MintPlayer.Dotnet.SocketExtensions/"]
RUN dotnet restore "./apps/CodeCoverage/CodeCoverage/CodeCoverage.csproj"
COPY . .

# Angular build through Nx: there is no per-app angular.json in this workspace, so `ng build` sees
# no workspace at all. The build target lives in
# apps/CodeCoverage/CodeCoverage/ClientApp/project.json.
#
# --skip-nx-cache is intentional. This Dockerfile produces the artifact that ships to production, so
# it must be hermetic: every byte rebuildable from the sources COPY'd above, with no possibility of
# a cached entry (potentially written by an earlier compromised CI run) being replayed into the
# deployed image. Cache speedups belong in the workflows, where the artifact is not what deploys.
# See docs/guide-nx-remote-cache.md.
WORKDIR /src
RUN npm ci
RUN npx nx run @spark-apps/code-coverage:build --configuration=production --skip-nx-cache

# Build .NET application
WORKDIR "/src/apps/CodeCoverage/CodeCoverage"
RUN dotnet build "./CodeCoverage.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
# EnableSpaBuilder=false: the SPA was already built above by Nx. Left on, the SpaServices targets
# would try to build it a second time from inside the publish.
RUN dotnet publish "./CodeCoverage.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:EnableSpaBuilder=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=build /src/apps/CodeCoverage/CodeCoverage/ClientApp/dist ./ClientApp/dist
ENTRYPOINT ["dotnet", "CodeCoverage.dll"]
