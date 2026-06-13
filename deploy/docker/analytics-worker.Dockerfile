# Veil.Analytics.Worker (log ingestion + nightly aggregation) — .NET build.
#
# Build context: the PARENT directory containing `veil` and `libraries`.
#
#   podman build -f veil/deploy/docker/analytics-worker.Dockerfile -t veil-analytics:latest .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY libraries/ ./libraries/
COPY veil/ ./veil/
RUN dotnet publish veil/src/Apps/Veil.Analytics.Worker/Veil.Analytics.Worker.csproj \
    -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN useradd -r -u 10001 veil
COPY --from=build /app ./
USER veil
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Veil.Analytics.Worker.dll"]
