# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/

# The build derives the served discovery template from this recording, so the build needs it.
# What StubID serves is the recording with the host substituted, never a document rebuilt from
# a model - that is how the members the broker leaves out stay left out.
COPY fixtures/neb/pp/CAP-001/response.raw fixtures/neb/pp/CAP-001/response.raw
RUN dotnet publish src/StubId.Server -c Release -o /app

# An empty directory to seed the key volume from. Docker initialises a named volume from the
# image, ownership included, and the runtime image has no shell to chown one afterwards.
RUN mkdir -p /keys-seed

# Run
#
# Chiselled: no shell, no package manager, nothing to attack. ICU is kept rather than
# switching on invariant globalisation, because the identities this serves carry Danish names
# and dates and a stub that mangles them is worse than a slightly larger image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .

# Keys are written on first use and read afterwards. Mount this if a restart has to be
# invisible: clients cache discovery metadata for hours, so regenerated keys fail every
# integration at once with nothing on their side to explain it.
COPY --from=build --chown=app:app /keys-seed /keys
VOLUME /keys
ENV StubId__KeyPath=/keys

ENV ASPNETCORE_URLS=http://+:8080
# 8443 answers only when StubId__Tls is set; the image serves plain HTTP by default.
EXPOSE 8080 8443

# Here rather than only in the workflow, so an image built from a clone carries them too.
#
# source is the load-bearing one: a registry links a package to its repository by this label,
# and that link is what makes the package page show where the image came from. The rest are
# what a reader of `docker inspect` needs to find the project without already knowing it.
#
# No version label. It is per-build, so the release workflow supplies it from the version
# property; a value written here would be a second place to forget.
LABEL org.opencontainers.image.title="StubID" \
      org.opencontainers.image.description="A stand-in for the test environments of the Danish MitID identity brokers, for running a login and signing integration in automated tests." \
      org.opencontainers.image.url="https://github.com/benne/stubid" \
      org.opencontainers.image.documentation="https://github.com/benne/stubid/blob/master/docs/guides/testcontainers.md" \
      org.opencontainers.image.source="https://github.com/benne/stubid" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.vendor="StubID contributors"

# Already non-root in the chiselled image.
ENTRYPOINT ["dotnet", "StubId.Server.dll"]
