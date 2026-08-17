# The serving image (ADR-016 §2).
#
# No GDAL and no Python, which is A-016's rule and ADR-015 §7's, and is trivially
# true for v1 because Q-88 cut both. When the job worker arrives it gets its own
# image and this one stays as it is — that separation is the whole reason there
# is more than one image.

# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0-noble AS build
WORKDIR /src

# Manifests first, so a source change does not re-download the world. There is
# no lock file to copy: central package management lives in Directory.Packages.
COPY Directory.Build.props Directory.Packages.props gis-server.sln ./
COPY src/ src/
COPY tests/ tests/

# Only the host, and only what it references. Building the solution here would
# pull the test projects and their packages into the image layer cache for no
# benefit.
RUN dotnet publish src/GisServer.Host/GisServer.Host.csproj \
      --configuration Release \
      --output /app \
      /p:UseAppHost=false

# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble AS runtime

# curl, for the container healthcheck and for looking around inside a running
# container when something is wrong. The aspnet image ships neither curl nor
# wget, so a healthcheck written against either fails permanently and the
# container is reported unhealthy while serving perfectly.
RUN apt-get update  && apt-get install --yes --no-install-recommends curl  && rm -rf /var/lib/apt/lists/*

# Non-root, and the uid is fixed rather than assigned. A volume written by uid
# 64198 on one host and read by a differently-numbered user on the next is the
# most common way persisted state becomes unreadable after a redeploy — which
# for us would mean the serving certificate (ADR-016 §3).
RUN groupadd --gid 64198 gisserver \
 && useradd --uid 64198 --gid 64198 --no-create-home --shell /usr/sbin/nologin gisserver \
 && mkdir -p /var/lib/gis-server \
 && chown 64198:64198 /var/lib/gis-server

WORKDIR /app
COPY --from=build /app ./

# ADR-016 §3's secret volume. Declared so that running without one is a visible
# choice rather than a silent loss of the certificate on every replacement.
VOLUME ["/var/lib/gis-server"]
ENV Graticula__StatePath=/var/lib/gis-server \
    Graticula__Listen=0.0.0.0 \
    DOTNET_gcServer=1

EXPOSE 8443
USER 64198:64198

# No automatic migration. ADR-016 §4b: an old image started by accident must not
# silently rewrite a newer schema, so the entrypoint serves and the operator runs
# `migrate --apply` deliberately.
ENTRYPOINT ["dotnet", "GisServer.Host.dll"]
