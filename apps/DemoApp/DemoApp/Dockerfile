FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
# Mount point for the Data Protection key ring. Outside Development Spark refuses to start unless the
# key ring is persisted, so run the image with a volume mounted here and
# Spark__DataProtection__KeysPath=/var/lib/demoapp/dataprotection-keys (or with
# Spark__DataProtection__Storage=RavenDb instead). Created as root and handed to `app` because a
# fresh named volume copies the mount point's ownership from the image: without this the directory
# would be root-owned and the non-root app could not write its first key.
RUN mkdir -p /var/lib/demoapp/dataprotection-keys && chown app:app /var/lib/demoapp/dataprotection-keys
USER app
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
# Node.js for the Angular build
RUN apt-get update && \
    apt-get -y install curl && \
    curl -sL https://deb.nodesource.com/setup_22.x | bash && \
    apt-get -y install nodejs && \
    rm -rf /var/lib/apt/lists/*

ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Selective csproj COPY for restore-layer caching. This must be DemoApp's full transitive
# ProjectReference closure, because `dotnet restore` below runs before `COPY . .` -- a missing entry
# fails the restore inside the image. demo-images-check.yml builds this file on every pull request
# that touches the closure; keep its DemoApp path filter in sync with this list.
COPY ["apps/DemoApp/DemoApp/DemoApp.csproj", "apps/DemoApp/DemoApp/"]
COPY ["apps/DemoApp/DemoApp.Library/DemoApp.Library.csproj", "apps/DemoApp/DemoApp.Library/"]
COPY ["libs/spark/MintPlayer.Spark/MintPlayer.Spark.csproj", "libs/spark/MintPlayer.Spark/"]
COPY ["libs/spark/MintPlayer.Spark.Abstractions/MintPlayer.Spark.Abstractions.csproj", "libs/spark/MintPlayer.Spark.Abstractions/"]
COPY ["libs/attributes/MintPlayer.Spark.Attributes/MintPlayer.Spark.Attributes.csproj", "libs/attributes/MintPlayer.Spark.Attributes/"]
COPY ["libs/controllers/MintPlayer.Spark.Controllers/MintPlayer.Spark.Controllers.csproj", "libs/controllers/MintPlayer.Spark.Controllers/"]
COPY ["libs/cron/MintPlayer.Spark.Cron/MintPlayer.Spark.Cron.csproj", "libs/cron/MintPlayer.Spark.Cron/"]
COPY ["libs/messaging/MintPlayer.Spark.Messaging/MintPlayer.Spark.Messaging.csproj", "libs/messaging/MintPlayer.Spark.Messaging/"]
COPY ["libs/messaging/MintPlayer.Spark.Messaging.Abstractions/MintPlayer.Spark.Messaging.Abstractions.csproj", "libs/messaging/MintPlayer.Spark.Messaging.Abstractions/"]
COPY ["libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/MintPlayer.Spark.SubscriptionWorker.Abstractions.csproj", "libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/"]
COPY ["libs/source_generators/MintPlayer.Spark.SourceGenerators/MintPlayer.Spark.SourceGenerators.csproj", "libs/source_generators/MintPlayer.Spark.SourceGenerators/"]
COPY ["libs/source_generators/MintPlayer.Spark.LibraryGenerators/MintPlayer.Spark.LibraryGenerators.csproj", "libs/source_generators/MintPlayer.Spark.LibraryGenerators/"]
RUN dotnet restore "./apps/DemoApp/DemoApp/DemoApp.csproj"
COPY . .

# Angular build through Nx: there is no per-app angular.json in this workspace, so `ng build` sees
# no workspace at all. The build target lives in apps/DemoApp/DemoApp/ClientApp/project.json.
# --skip-nx-cache keeps the image hermetic, as the CodeCoverage image does.
WORKDIR /src
RUN npm ci
RUN npx nx run @spark-demo/demo-app:build --configuration=production --skip-nx-cache

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
WORKDIR "/src/apps/DemoApp/DemoApp"
# No separate `dotnet build` step: publish compiles the app itself.
# EnableSpaBuilder=false: the SPA was already built above by Nx. Left on, the SpaServices targets
# would try to build it a second time from inside the publish.
RUN dotnet publish "./DemoApp.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:EnableSpaBuilder=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=build /src/apps/DemoApp/DemoApp/ClientApp/dist ./ClientApp/dist
ENTRYPOINT ["dotnet", "DemoApp.dll"]
