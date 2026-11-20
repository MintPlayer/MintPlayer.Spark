FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
# No Node.js at runtime: Iconic renders nothing on the server (the editor is a client-side SPA), unlike MintPlayer.
# Mount point for the Data Protection key ring (the compose file mounts a volume here and points
# Spark__DataProtection__KeysPath at it). Created as root and handed to `app` because a fresh named volume copies the
# mount point's ownership from the image: without this the non-root app could not write its first key.
RUN mkdir -p /var/lib/iconic/dataprotection-keys && chown app:app /var/lib/iconic/dataprotection-keys
USER app
WORKDIR /app
# 8080 only: Traefik's port detection picks the single exposed port (see apps/CodeCoverage/CodeCoverage/Dockerfile).
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
# No .git in the build context: the package versioning targets would only warn MPV003 per library (#565).
ENV MintPlayerVersioningSkip=true
# Node.js for the Angular build
RUN apt-get update && \
    apt-get -y install curl && \
    curl -sL https://deb.nodesource.com/setup_24.x | bash && \
    apt-get -y install nodejs && \
    rm -rf /var/lib/apt/lists/*

ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Selective csproj COPY for restore-layer caching. This must be Iconic's full transitive ProjectReference
# closure, because `dotnet restore` below runs before `COPY . .`: a missing entry fails the restore inside the image.
COPY ["apps/Iconic/Iconic/Iconic.csproj", "apps/Iconic/Iconic/"]
COPY ["apps/Iconic/Iconic.Library/Iconic.Library.csproj", "apps/Iconic/Iconic.Library/"]
COPY ["libs/attributes/MintPlayer.Spark.Attributes/MintPlayer.Spark.Attributes.csproj", "libs/attributes/MintPlayer.Spark.Attributes/"]
COPY ["libs/authorization/MintPlayer.Spark.Authorization/MintPlayer.Spark.Authorization.csproj", "libs/authorization/MintPlayer.Spark.Authorization/"]
COPY ["libs/authorization/MintPlayer.Spark.Authorization.Abstractions/MintPlayer.Spark.Authorization.Abstractions.csproj", "libs/authorization/MintPlayer.Spark.Authorization.Abstractions/"]
COPY ["libs/history/MintPlayer.Spark.History/MintPlayer.Spark.History.csproj", "libs/history/MintPlayer.Spark.History/"]
COPY ["libs/history/MintPlayer.Spark.History.Abstractions/MintPlayer.Spark.History.Abstractions.csproj", "libs/history/MintPlayer.Spark.History.Abstractions/"]
COPY ["libs/mail/MintPlayer.Spark.MailManager/MintPlayer.Spark.MailManager.csproj", "libs/mail/MintPlayer.Spark.MailManager/"]
COPY ["libs/mail/MintPlayer.Spark.MailManager.Abstractions/MintPlayer.Spark.MailManager.Abstractions.csproj", "libs/mail/MintPlayer.Spark.MailManager.Abstractions/"]
COPY ["libs/messaging/MintPlayer.Spark.Messaging/MintPlayer.Spark.Messaging.csproj", "libs/messaging/MintPlayer.Spark.Messaging/"]
COPY ["libs/messaging/MintPlayer.Spark.Messaging.Abstractions/MintPlayer.Spark.Messaging.Abstractions.csproj", "libs/messaging/MintPlayer.Spark.Messaging.Abstractions/"]
COPY ["libs/migrations/MintPlayer.Spark.Migrations/MintPlayer.Spark.Migrations.csproj", "libs/migrations/MintPlayer.Spark.Migrations/"]
COPY ["libs/model/MintPlayer.Spark.Model/MintPlayer.Spark.Model.csproj", "libs/model/MintPlayer.Spark.Model/"]
COPY ["libs/source_generators/MintPlayer.Spark.LibraryGenerators/MintPlayer.Spark.LibraryGenerators.csproj", "libs/source_generators/MintPlayer.Spark.LibraryGenerators/"]
COPY ["libs/source_generators/MintPlayer.Spark.SourceGenerators/MintPlayer.Spark.SourceGenerators.csproj", "libs/source_generators/MintPlayer.Spark.SourceGenerators/"]
COPY ["libs/spark/MintPlayer.Spark/MintPlayer.Spark.csproj", "libs/spark/MintPlayer.Spark/"]
COPY ["libs/spark/MintPlayer.Spark.SafeHttp/MintPlayer.Spark.SafeHttp.csproj", "libs/spark/MintPlayer.Spark.SafeHttp/"]
COPY ["libs/spark/MintPlayer.Spark.Abstractions/MintPlayer.Spark.Abstractions.csproj", "libs/spark/MintPlayer.Spark.Abstractions/"]
COPY ["libs/spark/MintPlayer.Spark.RavenDB/MintPlayer.Spark.RavenDB.csproj", "libs/spark/MintPlayer.Spark.RavenDB/"]
COPY ["libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/MintPlayer.Spark.SubscriptionWorker.Abstractions.csproj", "libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/"]
RUN dotnet restore "./apps/Iconic/Iconic/Iconic.csproj"
COPY . .

# Angular build through Nx (there is no per-app angular.json; the target is in ClientApp/project.json). It builds
# @mintplayer/constraint-solver first (a workspace dependency). --skip-nx-cache: this image is what ships to
# production, so it is built hermetically from the sources above, never from a cached entry (docs/guide-nx-remote-cache.md).
WORKDIR /src
RUN npm ci
RUN npx nx run @spark-apps/iconic:build --configuration=production --skip-nx-cache

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
WORKDIR "/src/apps/Iconic/Iconic"
# publish compiles the app itself; EnableSpaBuilder=false because Nx built the SPA above.
RUN dotnet publish "./Iconic.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:EnableSpaBuilder=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=build /src/apps/Iconic/Iconic/ClientApp/dist ./ClientApp/dist
ENTRYPOINT ["dotnet", "Iconic.dll"]
