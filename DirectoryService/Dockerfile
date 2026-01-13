FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 5129

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
WORKDIR "/src/DirectoryService"
RUN dotnet restore "DirectoryService.csproj"

# ✅ ПРАВИЛЬНЫЙ ПОРЯДОК: ENV ДО RUN dotnet ef
ENV PATH="$PATH:/root/.dotnet/tools"
RUN dotnet tool install --global dotnet-ef --version 9.0.0 || true

# ✅ ОТДЕЛЬНЫЙ RUN для bundle
RUN dotnet ef migrations bundle \
    --project "../DirectoryService.Infrastructure.Postgres/DirectoryService.Infrastructure.Postgres.csproj" \
    --startup-project "./DirectoryService.csproj" \
    --output /app/efbundle \
    --configuration Release \
    --verbose \
    --self-contained

# ✅ ОТДЕЛЬНЫЙ RUN для build
RUN dotnet build "./DirectoryService.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./DirectoryService.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=build /app/efbundle .
RUN chmod +x efbundle

ENTRYPOINT ["dotnet", "DirectoryService.dll"]
