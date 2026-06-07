FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER app
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Install Node.js for Angular build
RUN apt-get update && \
    apt-get -y install curl && \
    curl -sL https://deb.nodesource.com/setup_22.x | bash && \
    apt-get -y install nodejs && \
    rm -rf /var/lib/apt/lists/*

ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Selective csproj COPY for restore-layer caching. Paths must match the repo's
# libs/<group>/ layout and cover WebhooksDemo's full transitive ProjectReference
# closure, since `dotnet restore` (below) runs before `COPY . .`.
COPY ["Demo/WebhooksDemo/WebhooksDemo/WebhooksDemo.csproj", "Demo/WebhooksDemo/WebhooksDemo/"]
COPY ["Demo/WebhooksDemo/WebhooksDemo.Library/WebhooksDemo.Library.csproj", "Demo/WebhooksDemo/WebhooksDemo.Library/"]
COPY ["libs/spark/MintPlayer.Spark/MintPlayer.Spark.csproj", "libs/spark/MintPlayer.Spark/"]
COPY ["libs/spark/MintPlayer.Spark.Abstractions/MintPlayer.Spark.Abstractions.csproj", "libs/spark/MintPlayer.Spark.Abstractions/"]
COPY ["libs/authorization/MintPlayer.Spark.Authorization/MintPlayer.Spark.Authorization.csproj", "libs/authorization/MintPlayer.Spark.Authorization/"]
COPY ["libs/messaging/MintPlayer.Spark.Messaging/MintPlayer.Spark.Messaging.csproj", "libs/messaging/MintPlayer.Spark.Messaging/"]
COPY ["libs/messaging/MintPlayer.Spark.Messaging.Abstractions/MintPlayer.Spark.Messaging.Abstractions.csproj", "libs/messaging/MintPlayer.Spark.Messaging.Abstractions/"]
COPY ["libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/MintPlayer.Spark.SubscriptionWorker.Abstractions.csproj", "libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/"]
COPY ["libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/MintPlayer.Spark.Webhooks.GitHub.csproj", "libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/"]
COPY ["libs/webhooks/MintPlayer.Spark.Webhooks.GitHub.DevTunnel/MintPlayer.Spark.Webhooks.GitHub.DevTunnel.csproj", "libs/webhooks/MintPlayer.Spark.Webhooks.GitHub.DevTunnel/"]
COPY ["libs/source_generators/MintPlayer.Spark.SourceGenerators/MintPlayer.Spark.SourceGenerators.csproj", "libs/source_generators/MintPlayer.Spark.SourceGenerators/"]
COPY ["libs/socket_extensions/MintPlayer.Dotnet.SocketExtensions/MintPlayer.Dotnet.SocketExtensions.csproj", "libs/socket_extensions/MintPlayer.Dotnet.SocketExtensions/"]
RUN dotnet restore "./Demo/WebhooksDemo/WebhooksDemo/WebhooksDemo.csproj"
COPY . .

# Build Angular frontend via Nx (post-Nx-migration: there's no per-app
# angular.json, so `ng build` doesn't see a workspace; build target is
# defined in Demo/WebhooksDemo/WebhooksDemo/ClientApp/project.json).
#
# `--skip-nx-cache` is intentional. This Dockerfile produces the artifact
# that ships to production, so it must be hermetic: every byte must be
# rebuildable from the source files COPY'd above, with no possibility of a
# cached entry (potentially written by an earlier compromised CI run) being
# replayed into the deployed image. Cache speedups for non-deploy paths
# (PR validation, master publish-release) live in the GitHub Actions
# workflows, where the artifact isn't what gets deployed.
# See docs/guide-nx-remote-cache.md.
WORKDIR /src
RUN npm ci
RUN npx nx run @spark-demo/webhooks-demo:build --configuration=production --skip-nx-cache

# Build .NET application
WORKDIR "/src/Demo/WebhooksDemo/WebhooksDemo"
RUN dotnet build "./WebhooksDemo.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./WebhooksDemo.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:EnableSpaBuilder=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=build /src/Demo/WebhooksDemo/WebhooksDemo/ClientApp/dist ./ClientApp/dist
ENTRYPOINT ["dotnet", "WebhooksDemo.dll"]
