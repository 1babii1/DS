FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 5130

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY . .
WORKDIR "/src/AuthService/AuthService.Web"
RUN dotnet restore "AuthService.Web.csproj"

ENV PATH="$PATH:/root/.dotnet/tools"
RUN dotnet tool install --global dotnet-ef --version 10.0.5 || true

RUN dotnet ef migrations bundle \
    --project "../AuthService.Infrastructure.Postgres/AuthService.Infrastructure.Postgres.csproj" \
    --startup-project "./AuthService.Web.csproj" \
    --output /app/efbundle \
    --configuration Release \
    --self-contained

RUN dotnet build "AuthService.Web.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "AuthService.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=build /app/efbundle .
RUN chmod +x efbundle

ENTRYPOINT ["dotnet", "AuthService.Web.dll"]
