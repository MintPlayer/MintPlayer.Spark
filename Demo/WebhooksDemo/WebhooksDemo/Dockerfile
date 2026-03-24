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
COPY ["Demo/WebhooksDemo/WebhooksDemo/WebhooksDemo.csproj", "Demo/WebhooksDemo/WebhooksDemo/"]
COPY ["MintPlayer.Spark/MintPlayer.Spark.csproj", "MintPlayer.Spark/"]
COPY ["MintPlayer.Spark.Abstractions/MintPlayer.Spark.Abstractions.csproj", "MintPlayer.Spark.Abstractions/"]
COPY ["MintPlayer.Spark.SourceGenerators/MintPlayer.Spark.SourceGenerators.csproj", "MintPlayer.Spark.SourceGenerators/"]
COPY ["MintPlayer.Spark.Messaging/MintPlayer.Spark.Messaging.csproj", "MintPlayer.Spark.Messaging/"]
COPY ["MintPlayer.Spark.Messaging.Abstractions/MintPlayer.Spark.Messaging.Abstractions.csproj", "MintPlayer.Spark.Messaging.Abstractions/"]
COPY ["MintPlayer.Spark.SubscriptionWorker/MintPlayer.Spark.SubscriptionWorker.csproj", "MintPlayer.Spark.SubscriptionWorker/"]
COPY ["MintPlayer.Spark.Webhooks.GitHub/MintPlayer.Spark.Webhooks.GitHub.csproj", "MintPlayer.Spark.Webhooks.GitHub/"]
COPY ["MintPlayer.Spark.Webhooks.GitHub.DevTunnel/MintPlayer.Spark.Webhooks.GitHub.DevTunnel.csproj", "MintPlayer.Spark.Webhooks.GitHub.DevTunnel/"]
COPY ["MintPlayer.Dotnet.SocketExtensions/MintPlayer.Dotnet.SocketExtensions.csproj", "MintPlayer.Dotnet.SocketExtensions/"]
RUN dotnet restore "./Demo/WebhooksDemo/WebhooksDemo/WebhooksDemo.csproj"
COPY . .

# Build Angular frontend
WORKDIR /src
RUN npm install
WORKDIR /src/Demo/WebhooksDemo/WebhooksDemo/ClientApp
RUN npx ng build --configuration=production

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
