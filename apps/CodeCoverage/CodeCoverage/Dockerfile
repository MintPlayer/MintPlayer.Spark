FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER app
WORKDIR /app
# 8080 only (ASPNETCORE_HTTP_PORTS default): a second exposed port would make
# Traefik's port detection ambiguous. TLS terminates at Traefik.
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Node 22 for the Angular production build
RUN apt-get update && apt-get install -y curl ca-certificates gnupg \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y nodejs \
 && rm -rf /var/lib/apt/lists/*
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY CodeCoverage/CodeCoverage.csproj CodeCoverage/
COPY CodeCoverage.Library/CodeCoverage.Library.csproj CodeCoverage.Library/
RUN dotnet restore CodeCoverage/CodeCoverage.csproj
COPY . .
WORKDIR /src/CodeCoverage/ClientApp
RUN npm ci && npx ng build
WORKDIR /src/CodeCoverage
RUN dotnet build CodeCoverage.csproj -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
# EnableSpaBuilder=false: the Angular bundle was already built above
RUN dotnet publish CodeCoverage.csproj -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:EnableSpaBuilder=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=build /src/CodeCoverage/ClientApp/dist ./ClientApp/dist
ENTRYPOINT ["dotnet", "CodeCoverage.dll"]
