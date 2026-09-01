# Built from the repository root so the sample can reference the projects it depends on:
#   docker build -f samples/Signalboard/Dockerfile .
# The compose file in this directory sets that context for you.

# Pinned by digest, not by tag. A tag is a moving pointer, so `10.0-noble` today and `10.0-noble`
# next month are different images, and the build that was reviewed is not the image that ships.
# Dependabot watches this directory and raises the digest bumps, so pinning does not mean freezing.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src

# The whole source before restore, not the project files first. The usual manifests-first trick
# caches the restore layer, and it breaks here: a Blazor project's restore evaluates its static web
# assets, so restoring without wwwroot/ present produces a package list that disagrees with the
# committed lock file and --locked-mode fails. Correctness beats one cached layer in a sample.
COPY global.json Directory.Build.props Directory.Packages.props LakeWright.slnx ./
COPY src/ src/
COPY samples/Signalboard/ samples/Signalboard/

# Locked mode, for the same reason CI uses it: a transitive package must not change silently
# between the build that was reviewed and the image that ships.
RUN dotnet restore samples/Signalboard/Signalboard.csproj --locked-mode

RUN dotnet publish samples/Signalboard/Signalboard.csproj \
    --no-restore -c Release -o /app

# Chiseled: no shell, no package manager, and a non-root user already in place. There is nothing in
# this image to run except the application, which is the point.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:0839314d08bb65da369135389a5d8291f75ace587fbb0488f469eb92c62eef68 AS final
WORKDIR /app
COPY --from=build /app .

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

USER $APP_UID
ENTRYPOINT ["dotnet", "Signalboard.dll"]
