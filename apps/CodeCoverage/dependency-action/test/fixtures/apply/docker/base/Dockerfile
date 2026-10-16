# syntax=docker/dockerfile:1
FROM node:20.11.0-alpine AS build   # builder
WORKDIR /src
FROM mcr.microsoft.com/dotnet/aspnet:8.0.1@sha256:0000000000000000000000000000000000000000000000000000000000000000
COPY --from=build /src /app
