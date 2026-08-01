# Built from the repository root so the sample can reference the projects it depends on:
#   docker build -f samples/Signalboard/Dockerfile .
# The compose file in this directory sets that context for you.

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

USER $APP_UID
ENTRYPOINT ["dotnet", "Signalboard.dll"]
