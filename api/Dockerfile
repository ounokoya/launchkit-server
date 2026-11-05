# API LaunchKit. Build context: the root of the server repository (needs shared/i18n).
#   docker build -f api/Dockerfile -t launchkit-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY shared/i18n shared/i18n
COPY api/src/LaunchKit.Api/LaunchKit.Api.csproj api/src/LaunchKit.Api/
RUN dotnet restore api/src/LaunchKit.Api/LaunchKit.Api.csproj
COPY api/src api/src
RUN dotnet publish api/src/LaunchKit.Api/LaunchKit.Api.csproj -c Release -o /app --no-restore

# Chiseled runtime: no shell, no package manager, non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "LaunchKit.Api.dll"]
