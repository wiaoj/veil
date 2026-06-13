# Veil.Api (control plane) — multi-stage .NET build.
#
# Build context must be the PARENT directory containing both the `veil` and
# `wiaoj/libraries` repos side by side — Veil's projects reference the
# libraries sources via `..\..\..\libraries\...`:
#
#   podman build -f veil/deploy/docker/api.Dockerfile -t veil-api:latest .
# (run from the directory that holds veil/ and libraries/)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY libraries/ ./libraries/
COPY veil/ ./veil/
RUN dotnet publish veil/src/Apps/Veil.Api/Veil.Api.csproj \
    -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN useradd -r -u 10001 veil
COPY --from=build /app ./
USER veil
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Veil.Api.dll"]
