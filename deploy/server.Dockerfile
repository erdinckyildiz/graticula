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
COPY Directory.Build.props Directory.Packages.props graticula.sln ./
COPY src/ src/
COPY tests/ tests/

# <b>One file from outside `src/`, and leaving it out broke the build entirely —
# [D-169](../docs/architecture-debt.md).</b> `Graticula.Render.Skia` embeds
# `tools/fonts/DejaVuSans.ttf` as the face it draws labels with (ADR-027, D-161), so a
# context without it fails at compile with `CS1566: Error reading resource`. Measured
# 2026-08-26: no image had ever been built from this file, and it did not build.
# Copied narrowly rather than as `tools/`, which otherwise holds the register scripts
# and belongs in no serving image.
COPY tools/fonts/ tools/fonts/

# Only the host, and only what it references. Building the solution here would
# pull the test projects and their packages into the image layer cache for no
# benefit.
RUN dotnet publish src/Graticula.Host/Graticula.Host.csproj \
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
 && mkdir -p /var/lib/graticula \
 && chown 64198:64198 /var/lib/graticula

WORKDIR /app
COPY --from=build /app ./

# <b>The font is redistributed by this image, so its notice travels with it.</b>
# DejaVu Sans is compiled into `Graticula.Render.Skia.dll` rather than sitting beside
# it, which makes the image a redistribution of the font under the Bitstream Vera
# licence — and that licence's obligation is that the copyright notice accompanies it.
# `DEPENDENCY-LICENSES.md` already says the text ships in `tools/fonts`; this is the
# line that makes that true of the artefact somebody actually receives.
COPY tools/fonts/LICENSE-DejaVu.txt ./LICENSE-DejaVu.txt

# ADR-016 §3's secret volume. Declared so that running without one is a visible
# choice rather than a silent loss of the certificate on every replacement.
VOLUME ["/var/lib/graticula"]
ENV Graticula__StatePath=/var/lib/graticula \
    Graticula__Listen=0.0.0.0 \
    DOTNET_gcServer=1

EXPOSE 8443
USER 64198:64198

# <b>What this image is, for whoever inspects it rather than for whoever pulled it.</b>
# The release workflow passes the git tag and commit; a build from a working tree leaves them
# at `dev`, which is the honest answer for an image nobody released. It matters because
# [ADR-016](../docs/adr/ADR-016-packaging-deployment-upgrade.md) §4b's whole design turns on
# knowing which component is running, and a moving tag like `latest` cannot answer that -- the
# label can, and `docker inspect` reads it without starting anything.
ARG VERSION=dev
ARG REVISION=unknown

LABEL org.opencontainers.image.title="Graticula" \
      org.opencontainers.image.description="An ArcGIS-compatible GIS server over PostGIS." \
      org.opencontainers.image.source="https://github.com/erdinckyildiz/graticula" \
      org.opencontainers.image.licenses="Elastic-2.0" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"

# No automatic migration. ADR-016 §4b: an old image started by accident must not
# silently rewrite a newer schema, so the entrypoint serves and the operator runs
# `migrate --apply` deliberately.
ENTRYPOINT ["dotnet", "Graticula.Host.dll"]
