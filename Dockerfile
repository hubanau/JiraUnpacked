# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# 1. Build the SPA.
#
# This MUST finish before dotnet publish. Vite writes into the API's wwwroot,
# and UseStaticFiles no-ops on a missing directory — so publishing first
# yields an image that serves a working API with no UI and no error at all.
# That is the failure this ordering exists to prevent.
# ---------------------------------------------------------------------------
FROM node:24-alpine AS web
WORKDIR /src/web
COPY src/JiraUnpacked.Web/package.json src/JiraUnpacked.Web/package-lock.json ./
RUN npm ci
COPY src/JiraUnpacked.Web/ ./
RUN npm run build

# ---------------------------------------------------------------------------
# 2. Publish the API, with the SPA already in place.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS api
WORKDIR /src

# Restore against the project files alone, so a source-only change does not
# invalidate the restore layer.
COPY JiraUnpacked.sln ./
COPY src/JiraUnpacked.Api/JiraUnpacked.Api.csproj src/JiraUnpacked.Api/
COPY src/JiraUnpacked.Domain/JiraUnpacked.Domain.csproj src/JiraUnpacked.Domain/
COPY src/JiraUnpacked.Infrastructure/JiraUnpacked.Infrastructure.csproj src/JiraUnpacked.Infrastructure/
COPY tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj tests/JiraUnpacked.Tests/
RUN dotnet restore src/JiraUnpacked.Api/JiraUnpacked.Api.csproj

COPY src/ src/
COPY --from=web /src/JiraUnpacked.Api/wwwroot/ src/JiraUnpacked.Api/wwwroot/

RUN dotnet publish src/JiraUnpacked.Api/JiraUnpacked.Api.csproj \
      -c Release -o /app --no-restore

# Fail the build rather than ship a UI-less image. wwwroot is gitignored, so a
# COPY that silently matched nothing is a real possibility, and the resulting
# image would look healthy while serving JSON 404s to every browser.
RUN test -f /app/wwwroot/index.html \
    || (echo "SPA missing from wwwroot — the web stage did not produce a bundle" && exit 1)

# A self-contained migration bundle, shipped in the image so migrations run as a
# one-off task INSIDE the VPC. The alternative — applying them from CI — means
# exposing the database to GitHub's runners, which is a much worse trade.
#
# It is a separate binary because it needs a different DATABASE ROLE: the app
# runs as the RLS-enforced jiraunpacked_app (NOBYPASSRLS), which by design
# cannot create tables, manage policies, or install extensions. The bundle
# reads ConnectionStrings__Default through AppDbContextFactory, so the owner
# credentials arrive in the environment and never in argv.
RUN dotnet tool install --global dotnet-ef --version 8.* && \
    /root/.dotnet/tools/dotnet-ef migrations bundle \
      --project src/JiraUnpacked.Infrastructure \
      --startup-project src/JiraUnpacked.Infrastructure \
      --configuration Release --self-contained -r linux-musl-x64 \
      --output /app/migrate --force

# ---------------------------------------------------------------------------
# 3. Runtime.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

# IANA time-zone data. The Alpine runtime image ships none, so without it every "in period"
# filter saved in a zone other than UTC would be refused as an unknown time zone — in
# production only, since developer machines and CI runners carry zone data of their own.
# Installed as root, before USER app.
RUN apk add --no-cache tzdata

# Non-root. The .NET 8 images already ship an unprivileged `app` user, so this
# uses it rather than creating one — recreating it fails, since the group is
# already in use. The app needs no write access to its own directory: it serves
# static files, talks to Postgres and Jira, and logs to stdout.
COPY --from=api --chown=app:app /app ./
USER app

# Plain HTTP inside the container; TLS terminates at the load balancer. That is
# also why Security__TrustProxyHeaders must be set in the task definition —
# without it every request appears to come from the balancer and the per-IP
# auth rate limiter collapses into one shared bucket.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

ENTRYPOINT ["dotnet", "JiraUnpacked.Api.dll"]
