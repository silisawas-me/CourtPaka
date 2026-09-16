# syntax=docker/dockerfile:1
# Build on the runner's native platform, cross-publish for the target (amd64 / arm64).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY global.json ./
COPY src/CourtBooking.Api/CourtBooking.Api.csproj src/CourtBooking.Api/
RUN dotnet restore src/CourtBooking.Api/CourtBooking.Api.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/CourtBooking.Api/CourtBooking.Api.csproj -c Release -a $TARGETARCH --no-restore -o /app

# chiseled-extra includes ICU + tzdata, required for Asia/Bangkok and Thai culture (PRD 9.2).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CourtBooking.Api.dll"]
