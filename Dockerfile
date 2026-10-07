# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build stage
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG Channel=local
ARG BuildNumber=0
ARG BuildDate=
WORKDIR /src

# Restore first (better layer caching).
COPY ["global.json", "Directory.Build.props", "./"]
COPY ["src/MatMail/MatMail.csproj", "src/MatMail/"]
RUN dotnet restore "src/MatMail/MatMail.csproj"

COPY src/MatMail src/MatMail
# Fall back to today's date when BuildDate is not supplied, so the version never ends in "-".
RUN BUILD_DATE="${BuildDate:-$(date -u +%Y%m%d)}" && \
    dotnet publish "src/MatMail/MatMail.csproj" \
    -c Release -o /app/publish --no-restore \
    -p:UseAppHost=false \
    -p:Channel=$Channel -p:BuildNumber=$BuildNumber -p:BuildDate=$BUILD_DATE

# ---------------------------------------------------------------------------
# Runtime stage (ASP.NET Core base image)
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ASPNETCORE_HTTP_PORTS is emptied: the app opens its own listeners (web port from the configuration).
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS="" \
    MATMAIL_DATA=/data \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=1

# Config, keys, certificates: one volume.
RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /data
VOLUME ["/data"]

# 9933 web | 25 SMTP | 587 submission | 465 SMTPS | 143 IMAP | 993 IMAPS
EXPOSE 9933 25 587 465 143 993

COPY --from=build /app/publish .
USER $APP_UID

# The app checks itself (http or https as configured), so the image needs no extra tools.
HEALTHCHECK --interval=30s --timeout=8s --start-period=40s --retries=3 \
    CMD ["dotnet", "MatMail.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "MatMail.dll"]
