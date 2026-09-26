FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
# curl - единственная зависимость, добавленная сверх runtime-образа, нужна
# только для docker-compose HEALTHCHECK (/health/ready).
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
EXPOSE 5133

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
WORKDIR "/src/McpServer"
RUN dotnet restore "McpServer.csproj"
RUN dotnet build "McpServer.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "McpServer.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServer.dll"]
