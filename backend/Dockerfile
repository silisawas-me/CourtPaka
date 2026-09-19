# syntax=docker/dockerfile:1
# Build on the runner's native platform, cross-publish for the target (amd64 / arm64).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY global.json Directory.Build.props ./
COPY src/CourtBooking.Api/CourtBooking.Api.csproj src/CourtBooking.Api/
# One cache per architecture, and one writer at a time: a multi-arch build runs both stages at
# once, and sharing a package folder makes them delete each other's half-extracted packages.
RUN --mount=type=cache,id=nuget-$TARGETARCH,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/CourtBooking.Api/CourtBooking.Api.csproj -a $TARGETARCH

COPY src/ src/
RUN --mount=type=cache,id=nuget-$TARGETARCH,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/CourtBooking.Api/CourtBooking.Api.csproj -c Release -a $TARGETARCH --no-restore -o /app

# A named volume mounted here inherits this ownership, so the non-root runtime user can write the
# data protection key ring (without it every restart invalidates all auth cookies).
RUN mkdir -p /keys

# chiseled-extra includes ICU + tzdata, required for Asia/Bangkok and Thai culture (PRD 9.2).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
ARG APP_UID=1654
WORKDIR /app
COPY --from=build /app .
COPY --from=build --chown=$APP_UID:$APP_UID /keys /keys
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CourtBooking.Api.dll"]
