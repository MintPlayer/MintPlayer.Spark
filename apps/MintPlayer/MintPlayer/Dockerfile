FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
# Node.js at runtime: server rendering runs the Angular server bundle in a Node process (SpaServices.Prerendering,
# docs/spark_ssr_PRD.md D1). Without it every rendered page is a 500 while the API still answers.
RUN apt-get update && \
    apt-get -y install --no-install-recommends curl ca-certificates && \
    curl -sL https://deb.nodesource.com/setup_24.x | bash && \
    apt-get -y install --no-install-recommends nodejs && \
    apt-get -y purge curl && apt-get -y autoremove && \
    rm -rf /var/lib/apt/lists/*
# Mount point for the Data Protection key ring (the compose file mounts a volume here and points
# Spark__DataProtection__KeysPath at it). Created as root and handed to `app` because a fresh named volume copies the
# mount point's ownership from the image: without this the non-root app could not write its first key.
RUN mkdir -p /var/lib/mintplayer/dataprotection-keys && chown app:app /var/lib/mintplayer/dataprotection-keys
USER app
WORKDIR /app
# 8080 only: Traefik's port detection picks the single exposed port (see apps/CodeCoverage/CodeCoverage/Dockerfile).
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
# Node.js for the Angular build
RUN apt-get update && \
    apt-get -y install curl && \
    curl -sL https://deb.nodesource.com/setup_24.x | bash && \
    apt-get -y install nodejs && \
    rm -rf /var/lib/apt/lists/*

ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Selective csproj COPY for restore-layer caching. This must be MintPlayer's full transitive ProjectReference
# closure, because `dotnet restore` below runs before `COPY . .`: a missing entry fails the restore inside the image.
COPY ["apps/MintPlayer/MintPlayer/MintPlayer.csproj", "apps/MintPlayer/MintPlayer/"]
COPY ["apps/MintPlayer/MintPlayer.Library/MintPlayer.Library.csproj", "apps/MintPlayer/MintPlayer.Library/"]
COPY ["libs/attributes/MintPlayer.Spark.Attributes/MintPlayer.Spark.Attributes.csproj", "libs/attributes/MintPlayer.Spark.Attributes/"]
COPY ["libs/authorization/MintPlayer.Spark.Authorization/MintPlayer.Spark.Authorization.csproj", "libs/authorization/MintPlayer.Spark.Authorization/"]
COPY ["libs/boards/MintPlayer.Spark.Boards/MintPlayer.Spark.Boards.csproj", "libs/boards/MintPlayer.Spark.Boards/"]
COPY ["libs/authorization/MintPlayer.Spark.Authorization.Abstractions/MintPlayer.Spark.Authorization.Abstractions.csproj", "libs/authorization/MintPlayer.Spark.Authorization.Abstractions/"]
COPY ["libs/history/MintPlayer.Spark.History/MintPlayer.Spark.History.csproj", "libs/history/MintPlayer.Spark.History/"]
COPY ["libs/history/MintPlayer.Spark.History.Abstractions/MintPlayer.Spark.History.Abstractions.csproj", "libs/history/MintPlayer.Spark.History.Abstractions/"]
COPY ["libs/mail/MintPlayer.Spark.MailManager/MintPlayer.Spark.MailManager.csproj", "libs/mail/MintPlayer.Spark.MailManager/"]
COPY ["libs/mail/MintPlayer.Spark.MailManager.Abstractions/MintPlayer.Spark.MailManager.Abstractions.csproj", "libs/mail/MintPlayer.Spark.MailManager.Abstractions/"]
COPY ["libs/messaging/MintPlayer.Spark.Messaging/MintPlayer.Spark.Messaging.csproj", "libs/messaging/MintPlayer.Spark.Messaging/"]
COPY ["libs/messaging/MintPlayer.Spark.Messaging.Abstractions/MintPlayer.Spark.Messaging.Abstractions.csproj", "libs/messaging/MintPlayer.Spark.Messaging.Abstractions/"]
COPY ["libs/migrations/MintPlayer.Spark.Migrations/MintPlayer.Spark.Migrations.csproj", "libs/migrations/MintPlayer.Spark.Migrations/"]
COPY ["libs/model/MintPlayer.Spark.Model/MintPlayer.Spark.Model.csproj", "libs/model/MintPlayer.Spark.Model/"]
COPY ["libs/soft_delete/MintPlayer.Spark.SoftDelete/MintPlayer.Spark.SoftDelete.csproj", "libs/soft_delete/MintPlayer.Spark.SoftDelete/"]
COPY ["libs/soft_delete/MintPlayer.Spark.SoftDelete.Abstractions/MintPlayer.Spark.SoftDelete.Abstractions.csproj", "libs/soft_delete/MintPlayer.Spark.SoftDelete.Abstractions/"]
COPY ["libs/source_generators/MintPlayer.Spark.LibraryGenerators/MintPlayer.Spark.LibraryGenerators.csproj", "libs/source_generators/MintPlayer.Spark.LibraryGenerators/"]
COPY ["libs/source_generators/MintPlayer.Spark.SourceGenerators/MintPlayer.Spark.SourceGenerators.csproj", "libs/source_generators/MintPlayer.Spark.SourceGenerators/"]
COPY ["libs/spark/MintPlayer.Spark/MintPlayer.Spark.csproj", "libs/spark/MintPlayer.Spark/"]
COPY ["libs/spark/MintPlayer.Spark.SafeHttp/MintPlayer.Spark.SafeHttp.csproj", "libs/spark/MintPlayer.Spark.SafeHttp/"]
COPY ["libs/spark/MintPlayer.Spark.Abstractions/MintPlayer.Spark.Abstractions.csproj", "libs/spark/MintPlayer.Spark.Abstractions/"]
COPY ["libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/MintPlayer.Spark.SubscriptionWorker.Abstractions.csproj", "libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/"]
RUN dotnet restore "./apps/MintPlayer/MintPlayer/MintPlayer.csproj"
COPY . .

# Angular build through Nx (there is no per-app angular.json; the target is in ClientApp/project.json).
# --skip-nx-cache: this image is what ships to production, so it is built hermetically from the sources above,
# never from a cached entry (docs/guide-nx-remote-cache.md).
WORKDIR /src
RUN npm ci
RUN npx nx run @spark-apps/mint-player:build --configuration=production --skip-nx-cache

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
WORKDIR "/src/apps/MintPlayer/MintPlayer"
# publish compiles the app itself; EnableSpaBuilder=false because Nx built the SPA above.
RUN dotnet publish "./MintPlayer.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:EnableSpaBuilder=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=build /src/apps/MintPlayer/MintPlayer/ClientApp/dist ./ClientApp/dist
ENTRYPOINT ["dotnet", "MintPlayer.dll"]
