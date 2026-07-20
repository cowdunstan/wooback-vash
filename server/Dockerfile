# ── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Optional: trust extra root CAs before any HTTPS (nuget restore). This is for
# LOCAL builds behind a TLS-inspecting proxy or antivirus (e.g. AVG Web Shield),
# which re-signs traffic with a private root the base image doesn't trust. Drop
# the root cert as a .crt into ca-certs/ (see ca-certs/README.md). On Fly.io/CI
# the directory holds only .gitkeep, so update-ca-certificates is a no-op.
COPY ca-certs/ /usr/local/share/ca-certificates/extra/
RUN update-ca-certificates

# Restore first (cached unless the csproj changes).
COPY WoobackVash.Api/WoobackVash.Api.csproj WoobackVash.Api/
RUN dotnet restore WoobackVash.Api/WoobackVash.Api.csproj

# Copy the rest and publish.
COPY WoobackVash.Api/ WoobackVash.Api/
RUN dotnet publish WoobackVash.Api/WoobackVash.Api.csproj -c Release -o /app --no-restore

# ── Runtime stage ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Same optional CA trust for outbound HTTPS at runtime (Discord/Raid-Helper/WCL)
# when running the container locally behind the proxy/AV. No-op on Fly/CI.
COPY ca-certs/ /usr/local/share/ca-certificates/extra/
RUN update-ca-certificates

COPY --from=build /app .

# Kestrel listens on 8080 (matches fly.toml internal_port).
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "WoobackVash.Api.dll"]
