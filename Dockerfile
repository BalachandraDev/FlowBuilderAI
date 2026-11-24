# FlowCapture: the API and the web app in one image, one address. Build from the repository root:  docker build -t flowcapture .

# 1. the web app (React). Vite writes it to ../src/FlowCapture.Api/wwwroot, which is /src/FlowCapture.Api/wwwroot here.
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

# 2. the server, with the built web app inside
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/FlowCapture.Domain/*.csproj src/FlowCapture.Domain/
COPY src/FlowCapture.Shared/*.csproj src/FlowCapture.Shared/
COPY src/FlowCapture.Application/*.csproj src/FlowCapture.Application/
COPY src/FlowCapture.Infrastructure/*.csproj src/FlowCapture.Infrastructure/
COPY src/FlowCapture.Api/*.csproj src/FlowCapture.Api/
RUN dotnet restore src/FlowCapture.Api/FlowCapture.Api.csproj
COPY src/FlowCapture.Domain src/FlowCapture.Domain
COPY src/FlowCapture.Shared src/FlowCapture.Shared
COPY src/FlowCapture.Application src/FlowCapture.Application
COPY src/FlowCapture.Infrastructure src/FlowCapture.Infrastructure
COPY src/FlowCapture.Api src/FlowCapture.Api
COPY --from=web /src/FlowCapture.Api/wwwroot src/FlowCapture.Api/wwwroot
COPY prompts prompts
COPY templates templates
RUN dotnet publish src/FlowCapture.Api/FlowCapture.Api.csproj -c Release -o /app --no-restore -p:SkipWebBuild=true

# 3. the image that runs
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
# Chromium draws the diagrams, LibreOffice makes the PDF, the fonts keep the PDF looking like the Word file
# (Liberation = Arial, Carlito = Calibri, DejaVu as a safe fallback).
RUN sed -i 's|http://deb.debian.org|https://deb.debian.org|g' /etc/apt/sources.list.d/debian.sources \
 && apt-get update \
 && apt-get install -y --no-install-recommends -o Acquire::Retries=5 chromium libreoffice-writer libreoffice-java-common- \
      fonts-liberation fonts-crosextra-carlito fonts-dejavu-core ca-certificates \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data/blobs && chown -R app:app /data
USER app

# (the base image already listens on HTTP port 8080)
ENV ASPNETCORE_ENVIRONMENT=Production \
    Storage__RootPath=/data/blobs \
    Diagrams__BrowserPath=/usr/bin/chromium \
    Diagrams__NoSandbox=true \
    Documents__LibreOfficePath=/usr/bin/soffice \
    HOME=/tmp
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "FlowCapture.Api.dll"]
