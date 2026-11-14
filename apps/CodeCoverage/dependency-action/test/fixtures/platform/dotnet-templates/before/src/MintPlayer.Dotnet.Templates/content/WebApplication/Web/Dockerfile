# Stage 1: Build Angular app
FROM node:22-alpine AS node-build
WORKDIR /src/ClientApp
COPY Web/ClientApp/package*.json ./
RUN npm ci
COPY Web/ClientApp/ ./
RUN npm run build -- --configuration production

# Stage 2: Build .NET app
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS dotnet-build
WORKDIR /src
COPY ["Web/MintPlayer.Dotnet.WebApplication.Web.csproj", "Web/"]
COPY ["Data/MintPlayer.Dotnet.WebApplication.Data.csproj", "Data/"]
COPY ["Dtos/MintPlayer.Dotnet.WebApplication.Dtos.csproj", "Dtos/"]
RUN dotnet restore "Web/MintPlayer.Dotnet.WebApplication.Web.csproj"
COPY . .
COPY --from=node-build /src/ClientApp/dist /src/Web/ClientApp/dist
WORKDIR "/src/Web"
RUN dotnet build "MintPlayer.Dotnet.WebApplication.Web.csproj" -c Release -o /app/build

# Stage 3: Publish
FROM dotnet-build AS publish
RUN dotnet publish "MintPlayer.Dotnet.WebApplication.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 4: Final image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 80
EXPOSE 443
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MintPlayer.Dotnet.WebApplication.Web.dll"]
