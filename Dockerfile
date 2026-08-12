# tenant-pulse container image.
#
# Runs the simulator continuously in Azure Container Apps. Deliberately carries no configuration
# and no secrets: everything comes from environment variables and Container Apps secrets at run
# time, so the image is safe to push to a registry.
#
# The storyline catalogue IS baked in — it is data, not secrets, and the simulator needs it to plan.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, against just the project graph, so code edits don't invalidate the package layer.
# global.json pins the SDK feature band, so it has to be present before any dotnet command runs.
COPY src/global.json src/Directory.Packages.props src/Directory.Build.props src/TenantPulse.slnx ./
COPY src/TenantPulse.Core/TenantPulse.Core.csproj TenantPulse.Core/
COPY src/TenantPulse.Engine/TenantPulse.Engine.csproj TenantPulse.Engine/
COPY src/TenantPulse.Cli/TenantPulse.Cli.csproj TenantPulse.Cli/
RUN dotnet restore TenantPulse.Cli/TenantPulse.Cli.csproj

COPY src/TenantPulse.Core/ TenantPulse.Core/
COPY src/TenantPulse.Engine/ TenantPulse.Engine/
COPY src/TenantPulse.Cli/ TenantPulse.Cli/

RUN dotnet publish TenantPulse.Cli/TenantPulse.Cli.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Never run as root: the mounted state volume holds refresh tokens.
RUN useradd --create-home --uid 10001 pulse
COPY --from=build /app/publish ./
COPY config/storylines.json ./config/storylines.json

# .state holds the SQLite journal and the token caches. Mount a volume here in Container Apps so
# both survive a restart — without the journal, purge cannot clean the tenant up afterwards.
RUN mkdir -p /app/.state && chown -R pulse:pulse /app
VOLUME /app/.state
USER pulse

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 \
    TENANTPULSE_TenantPulse__Simulation__JournalPath=/app/.state/journal.db \
    TENANTPULSE_TenantPulse__Simulation__KillSwitchFile=/app/.state/STOP \
    TENANTPULSE_TenantPulse__Auth__CacheDirectory=/app/.state/token-cache

ENTRYPOINT ["dotnet", "tenant-pulse.dll"]
CMD ["run", "--live"]
