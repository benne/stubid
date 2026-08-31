# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/

# The recorded discovery document is compiled into the binary, so the build needs it. What
# StubID serves is the recording with the host substituted, never a document rebuilt from a
# model - that is how the members the broker leaves out stay left out.
COPY fixtures/neb/pp/CAP-001/ fixtures/neb/pp/CAP-001/
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
EXPOSE 8080

# Already non-root in the chiselled image.
ENTRYPOINT ["dotnet", "StubId.Server.dll"]
