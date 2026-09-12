FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 5132

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
WORKDIR "/src/AuditService/AuditService.Web"
RUN dotnet restore "AuditService.Web.csproj"

ENV PATH="$PATH:/root/.dotnet/tools"
RUN dotnet tool install --global dotnet-ef --version 10.0.5 || true

RUN dotnet ef migrations bundle \
    --project "../AuditService.Infrastructure/AuditService.Infrastructure.csproj" \
    --startup-project "./AuditService.Web.csproj" \
    --output /app/efbundle \
    --configuration Release \
    --self-contained

RUN dotnet build "AuditService.Web.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "AuditService.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=build /app/efbundle .
RUN chmod +x efbundle

ENTRYPOINT ["dotnet", "AuditService.Web.dll"]
