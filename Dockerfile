# =========================
# Build stage
# =========================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY *.csproj ./
RUN dotnet restore

# Copy everything else and build
COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# =========================
# Runtime stage
# =========================
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

# Create directory for SQLite (persistent)
RUN mkdir -p /data

# Copy published output
COPY --from=build /app/publish .

# Environment variables (defaults)
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV SQLITE_DB_PATH=/data/processed_messages.db
ENV AUTH_STORE_PATH=/data/Auth.txt
ENV POLL_MSGS_DELAY_MS=30000
ENV FAIL_POLL_MSGS_DELAY_MS=1800000
ENV POLL_REQS_DELAY_MS=100000
ENV FAIL_POLL_REQS_DELAY_MS=1800000
#ENV USERNAME=to be passed through env at runtime
#ENV PASSWORD=to be passed through env at runtime



# Expose nothing (console app)

# Run app
ENTRYPOINT ["dotnet", "IGMediaDownloaderV2.dll"]
