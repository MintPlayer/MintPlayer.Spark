FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
# Mount point for the Data Protection key ring (the compose file, M12, mounts a volume here and points
# Spark__DataProtection__KeysPath at it). Created as root and handed to `app` because a fresh named volume copies the
# mount point's ownership from the image: without this the non-root app could not write its first key.
RUN mkdir -p /var/lib/seotester/dataprotection-keys && chown app:app /var/lib/seotester/dataprotection-keys
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
# Selective csproj COPY for restore-layer caching. This must be SeoTester's full transitive ProjectReference
# closure, because `dotnet restore` below runs before `COPY . .`: a missing entry fails the restore inside the image.
COPY ["apps/SeoTester/SeoTester/SeoTester.csproj", "apps/SeoTester/SeoTester/"]
COPY ["apps/SeoTester/SeoTester.Fetching/SeoTester.Fetching.csproj", "apps/SeoTester/SeoTester.Fetching/"]
COPY ["apps/SeoTester/SeoTester.Library/SeoTester.Library.csproj", "apps/SeoTester/SeoTester.Library/"]
COPY ["libs/attributes/MintPlayer.Spark.Attributes/MintPlayer.Spark.Attributes.csproj", "libs/attributes/MintPlayer.Spark.Attributes/"]
COPY ["libs/authorization/MintPlayer.Spark.Authorization/MintPlayer.Spark.Authorization.csproj", "libs/authorization/MintPlayer.Spark.Authorization/"]
COPY ["libs/authorization/MintPlayer.Spark.Authorization.Abstractions/MintPlayer.Spark.Authorization.Abstractions.csproj", "libs/authorization/MintPlayer.Spark.Authorization.Abstractions/"]
COPY ["libs/cron/MintPlayer.Spark.Cron/MintPlayer.Spark.Cron.csproj", "libs/cron/MintPlayer.Spark.Cron/"]
COPY ["libs/history/MintPlayer.Spark.History/MintPlayer.Spark.History.csproj", "libs/history/MintPlayer.Spark.History/"]
COPY ["libs/history/MintPlayer.Spark.History.Abstractions/MintPlayer.Spark.History.Abstractions.csproj", "libs/history/MintPlayer.Spark.History.Abstractions/"]
COPY ["libs/mail/MintPlayer.Spark.MailManager/MintPlayer.Spark.MailManager.csproj", "libs/mail/MintPlayer.Spark.MailManager/"]
COPY ["libs/mail/MintPlayer.Spark.MailManager.Abstractions/MintPlayer.Spark.MailManager.Abstractions.csproj", "libs/mail/MintPlayer.Spark.MailManager.Abstractions/"]
COPY ["libs/messaging/MintPlayer.Spark.Messaging/MintPlayer.Spark.Messaging.csproj", "libs/messaging/MintPlayer.Spark.Messaging/"]
COPY ["libs/messaging/MintPlayer.Spark.Messaging.Abstractions/MintPlayer.Spark.Messaging.Abstractions.csproj", "libs/messaging/MintPlayer.Spark.Messaging.Abstractions/"]
COPY ["libs/migrations/MintPlayer.Spark.Migrations/MintPlayer.Spark.Migrations.csproj", "libs/migrations/MintPlayer.Spark.Migrations/"]
COPY ["libs/model/MintPlayer.Spark.Model/MintPlayer.Spark.Model.csproj", "libs/model/MintPlayer.Spark.Model/"]
COPY ["libs/notifications/MintPlayer.Spark.Notifications/MintPlayer.Spark.Notifications.csproj", "libs/notifications/MintPlayer.Spark.Notifications/"]
COPY ["libs/organizations/MintPlayer.Spark.Organizations/MintPlayer.Spark.Organizations.csproj", "libs/organizations/MintPlayer.Spark.Organizations/"]
COPY ["libs/settings/MintPlayer.Spark.Settings/MintPlayer.Spark.Settings.csproj", "libs/settings/MintPlayer.Spark.Settings/"]
COPY ["libs/source_generators/MintPlayer.Spark.LibraryGenerators/MintPlayer.Spark.LibraryGenerators.csproj", "libs/source_generators/MintPlayer.Spark.LibraryGenerators/"]
COPY ["libs/source_generators/MintPlayer.Spark.SourceGenerators/MintPlayer.Spark.SourceGenerators.csproj", "libs/source_generators/MintPlayer.Spark.SourceGenerators/"]
COPY ["libs/spark/MintPlayer.Spark/MintPlayer.Spark.csproj", "libs/spark/MintPlayer.Spark/"]
COPY ["libs/spark/MintPlayer.Spark.SafeHttp/MintPlayer.Spark.SafeHttp.csproj", "libs/spark/MintPlayer.Spark.SafeHttp/"]
COPY ["libs/spark/MintPlayer.Spark.Abstractions/MintPlayer.Spark.Abstractions.csproj", "libs/spark/MintPlayer.Spark.Abstractions/"]
COPY ["libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/MintPlayer.Spark.SubscriptionWorker.Abstractions.csproj", "libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/"]
COPY ["libs/webpush/MintPlayer.Spark.WebPush/MintPlayer.Spark.WebPush.csproj", "libs/webpush/MintPlayer.Spark.WebPush/"]
RUN dotnet restore "./apps/SeoTester/SeoTester/SeoTester.csproj"
COPY . .

# Angular build through Nx (there is no per-app angular.json; the target is in ClientApp/project.json).
# --skip-nx-cache: this image is what ships to production, so it is built hermetically from the sources above,
# never from a cached entry (docs/guide-nx-remote-cache.md).
WORKDIR /src
RUN npm ci
RUN npx nx run @spark-apps/seo-tester:build --configuration=production --skip-nx-cache

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
WORKDIR "/src/apps/SeoTester/SeoTester"
# publish compiles the app itself; EnableSpaBuilder=false because Nx built the SPA above.
RUN dotnet publish "./SeoTester.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:EnableSpaBuilder=false
# Playwright's driver (node) is published executable for its OWNER only (0744, root), but the app runs as `app`:
# every render then failed "Permission denied" starting /app/.playwright/node/linux-x64/node (production, 2026-10-11).
# a+rX: readable by all, and executable by all where it was executable already; nothing else becomes executable.
RUN chmod -R a+rX /app/publish/.playwright

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=build /src/apps/SeoTester/SeoTester/ClientApp/dist ./ClientApp/dist
ENTRYPOINT ["dotnet", "SeoTester.dll"]
