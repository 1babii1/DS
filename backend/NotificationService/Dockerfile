FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# curl - the only dependency added on top of the runtime image, needed only
# for the docker-compose HEALTHCHECK (/health/ready).
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
EXPOSE 5133

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
WORKDIR "/src/NotificationService/NotificationService.Web"
RUN dotnet restore "NotificationService.Web.csproj"

ENV PATH="$PATH:/root/.dotnet/tools"
RUN dotnet tool install --global dotnet-ef --version 10.0.5 || true

RUN dotnet ef migrations bundle \
    --project "../NotificationService.Infrastructure.Postgres/NotificationService.Infrastructure.Postgres.csproj" \
    --startup-project "./NotificationService.Web.csproj" \
    --output /app/efbundle \
    --configuration Release \
    --self-contained

RUN dotnet build "NotificationService.Web.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "NotificationService.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=build /app/efbundle .
RUN chmod +x efbundle

ENTRYPOINT ["dotnet", "NotificationService.Web.dll"]
