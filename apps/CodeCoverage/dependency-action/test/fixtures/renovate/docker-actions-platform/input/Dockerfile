FROM node:20.11.0-alpine AS build
RUN echo executed > /sentinel/dockerfile-run
FROM mcr.microsoft.com/dotnet/sdk:8.0.100 AS sdk
FROM mcr.microsoft.com/dotnet/aspnet:8.0
FROM python:3.11-slim@sha256:0000000000000000000000000000000000000000000000000000000000000000
FROM ubuntu:22.04
