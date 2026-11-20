FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY NuGet.config ./
COPY vendor/packages/ vendor/packages/
COPY TermLume.Api/TermLume.Api.csproj TermLume.Api/
COPY TermLume.Shared/TermLume.Shared.csproj TermLume.Shared/
RUN dotnet restore TermLume.Api/TermLume.Api.csproj
COPY TermLume.Api/ TermLume.Api/
COPY TermLume.Shared/ TermLume.Shared/
COPY aegis-iam.json workspace-api.json ./
RUN dotnet publish TermLume.Api/TermLume.Api.csproj -c Release --no-restore --self-contained false -o /out

# Keep this patch aligned with RuntimeFrameworkVersion; refresh OS layers when rebuilding.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15
WORKDIR /app
LABEL org.opencontainers.image.title="TermLume Workspace API" \
      org.opencontainers.image.version="0.9.0" \
      org.opencontainers.image.description="ABP Framework workspace service with Aegis-IAM and PostgreSQL"
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD ["dotnet", "TermLume.Api.dll", "--health-check"]
ENTRYPOINT ["dotnet", "TermLume.Api.dll"]
CMD ["--settings", "/run/secrets/settings.json"]
