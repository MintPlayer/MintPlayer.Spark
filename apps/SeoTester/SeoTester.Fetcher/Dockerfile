# SeoTester's fetcher (docs/seo_analyzer_PRD.md D54): the only container that connects to the websites SeoTester
# audits. No database client, no SPA, no Node: just the SSRF-safe clients, the TLS probes and DNS behind an internal
# API. Built from the repository root (context .), like the app's image.
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
USER app
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Selective csproj COPY for restore-layer caching: the fetcher's full transitive ProjectReference closure
# (tools/verify-seotester-deploy.test.mjs keeps this list equal to the csproj graph, and that graph free of RavenDB).
COPY ["apps/SeoTester/SeoTester.Fetcher/SeoTester.Fetcher.csproj", "apps/SeoTester/SeoTester.Fetcher/"]
COPY ["apps/SeoTester/SeoTester.Fetching/SeoTester.Fetching.csproj", "apps/SeoTester/SeoTester.Fetching/"]
COPY ["apps/SeoTester/SeoTester.Library/SeoTester.Library.csproj", "apps/SeoTester/SeoTester.Library/"]
COPY ["libs/attributes/MintPlayer.Spark.Attributes/MintPlayer.Spark.Attributes.csproj", "libs/attributes/MintPlayer.Spark.Attributes/"]
COPY ["libs/messaging/MintPlayer.Spark.Messaging.Abstractions/MintPlayer.Spark.Messaging.Abstractions.csproj", "libs/messaging/MintPlayer.Spark.Messaging.Abstractions/"]
COPY ["libs/source_generators/MintPlayer.Spark.LibraryGenerators/MintPlayer.Spark.LibraryGenerators.csproj", "libs/source_generators/MintPlayer.Spark.LibraryGenerators/"]
COPY ["libs/source_generators/MintPlayer.Spark.SourceGenerators/MintPlayer.Spark.SourceGenerators.csproj", "libs/source_generators/MintPlayer.Spark.SourceGenerators/"]
COPY ["libs/spark/MintPlayer.Spark.SafeHttp/MintPlayer.Spark.SafeHttp.csproj", "libs/spark/MintPlayer.Spark.SafeHttp/"]
RUN dotnet restore "./apps/SeoTester/SeoTester.Fetcher/SeoTester.Fetcher.csproj"
COPY . .

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
WORKDIR "/src/apps/SeoTester/SeoTester.Fetcher"
RUN dotnet publish "./SeoTester.Fetcher.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# The compose file runs this read-only with /tmp as tmpfs; no diagnostics IPC socket is needed.
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "SeoTester.Fetcher.dll"]
