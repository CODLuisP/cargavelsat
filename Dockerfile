# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/RestauracionGps/RestauracionGps.csproj RestauracionGps/
RUN dotnet restore RestauracionGps/RestauracionGps.csproj
COPY src/RestauracionGps/ RestauracionGps/
RUN dotnet publish RestauracionGps/RestauracionGps.csproj -c Release -o /app --no-restore /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0

# tzdata: zona America/Lima. curl: healthcheck.
# (Los dumps se leen con GZipStream dentro de .NET: no hace falta mysql-client ni gzip.)
RUN apt-get update \
 && apt-get install -y --no-install-recommends tzdata curl \
 && rm -rf /var/lib/apt/lists/*

ENV TZ=America/Lima \
    ASPNETCORE_HTTP_PORTS=8080 \
    Restauracion__DirectorioTemporal=/data/tmp

# Carpeta de descargas temporales (se monta como volumen en docker-compose).
RUN mkdir -p /data/tmp && chown -R $APP_UID /data/tmp

WORKDIR /app
COPY --from=build /app .

USER $APP_UID
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
  CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "RestauracionGps.dll"]
