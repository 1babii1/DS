FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# curl - единственная зависимость, добавленная сверх runtime-образа, нужна
# только для docker-compose HEALTHCHECK (/health/ready).
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
EXPOSE 5134

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
WORKDIR "/src/RewardsService/RewardsService.Web"
RUN dotnet restore "RewardsService.Web.csproj"

ENV PATH="$PATH:/root/.dotnet/tools"
RUN dotnet tool install --global dotnet-ef --version 10.0.5 || true

RUN dotnet ef migrations bundle \
    --project "../RewardsService.Infrastructure/RewardsService.Infrastructure.csproj" \
    --startup-project "./RewardsService.Web.csproj" \
    --output /app/efbundle \
    --configuration Release \
    --self-contained

RUN dotnet build "RewardsService.Web.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "RewardsService.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=build /app/efbundle .
RUN chmod +x efbundle

ENTRYPOINT ["dotnet", "RewardsService.Web.dll"]
