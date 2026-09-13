using Shared.Middlewares;
using Shared.Cors;
using DirectoryService.Application.Database;
using DirectoryService.Application.Department.Commands;
using DirectoryService.Application.Department.Queries;
using DirectoryService.Application.Location.Commands;
using DirectoryService.Application.Location.Queries;
using DirectoryService.Application.Position;
using DirectoryService.Application.Position.Queries;
using DirectoryService.Grpc;
using DirectoryService.Infrastructure.Postgres;
using DirectoryService.Infrastructure.Postgres.Backgrounds;
using DirectoryService.Infrastructure.Postgres.Database;
using DirectoryService.Infrastructure.Postgres.Repositories.Departments;
using DirectoryService.Infrastructure.Postgres.Repositories.Locations;
using DirectoryService.Infrastructure.Postgres.Repositories.Positions;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using DirectoryService.Application.Search;
using DirectoryService.Infrastructure.Postgres.Embeddings;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Serilog;
using Shared;
using Shared.Outbox;

var builder = WebApplication.CreateBuilder(args);

// Cleartext HTTP/1.1+HTTP/2 multiplexing on one Kestrel endpoint (no TLS, no ALPN) is
// unreliable in practice - the server can reject h2c requests with HTTP_1_1_REQUIRED.
// Separate ports instead: REST (via nginx) stays HTTP/1.1, gRPC (internal only, called
// directly by other services like EmployeeService) gets its own HTTP/2-only endpoint.
// Explicit Listen calls make Kestrel ignore ASPNETCORE_URLS entirely, so both ports are
// controlled here.
var restPort = builder.Configuration.GetValue("Kestrel:RestPort", 5129);
var grpcPort = builder.Configuration.GetValue("Kestrel:GrpcPort", 5179);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(restPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
    options.ListenAnyIP(grpcPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
});

builder.Logging.ClearProviders();

builder.Logging.AddConsole();

builder.Logging.SetMinimumLevel(LogLevel.Debug);

builder.Host.UseSerilog((context, _, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

builder.Services.AddOpenApi();

builder.Services.AddFrameworkCors(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddEnvelopeModelStateValidation();

builder.Services.AddGrpc();

builder.Services.AddHttpLogging();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.MetadataAddress = builder.Configuration["Auth:MetadataAddress"];
        options.RequireHttpsMetadata = builder.Environment.IsProduction();
        options.TokenValidationParameters.ValidIssuer = builder.Configuration["Auth:Issuer"];
        options.TokenValidationParameters.ValidateAudience = false;
        options.TokenValidationParameters.RoleClaimType = "role";
        options.TokenValidationParameters.NameClaimType = "name";
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("CanEdit", policy => policy.RequireRole("admin", "editor"));

builder.Services.AddValidatorsFromAssemblyContaining<CreateDepartmentValidation>();

builder.Services.AddSingleton<IConfigureOptions<JsonOptions>, InjectJSONSerializeConfig>();

builder.Services.AddScoped<DirectoryServiceDbContext>(_ =>
    new DirectoryServiceDbContext(builder.Configuration.GetConnectionString("DirectoryServiceDb")!));

builder.Services.AddScoped<IReadDbContext, DirectoryServiceDbContext>(_ =>
    new DirectoryServiceDbContext(builder.Configuration.GetConnectionString("DirectoryServiceDb")!));

builder.Services.Configure<ClearDbOptions>(builder.Configuration.GetSection("ClearDbOptions"));

builder.Services.AddHostedService<ClearDbOfDeletedEntities>();

builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

builder.Services.AddScoped<ITransactionManager, TransactionManager>();

builder.Services.AddScoped<IOutboxWriter, OutboxWriter>();
builder.Services.AddOutboxPublisher<DirectoryServiceDbContext>(builder.Configuration, "directory.events");

builder.Services.AddOptions<EmbeddingsOptions>()
    .Bind(builder.Configuration.GetSection(EmbeddingsOptions.SectionName));
builder.Services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<EmbeddingsOptions>>().Value;
    client.BaseAddress = new Uri(options.OllamaBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHostedService<DepartmentEmbeddingWorker>();
builder.Services.AddScoped<IDepartmentSemanticSearch, DepartmentSemanticSearchService>();
builder.Services.AddScoped<SearchDepartmentsSemanticHandler>();

builder.Services.AddScoped<ILocationsRepository, EfCoreLocationsRepository>();

builder.Services.AddScoped<IPositionRepository, EfCorePositionRepository>();

builder.Services.AddScoped<IDepartmentRepository, EfCoreDepartmentsRepository>();

builder.Services.AddScoped<CreateLocationHandle>();

builder.Services.AddScoped<CreatePositionHandle>();

builder.Services.AddScoped<CreateDepartmentHandler>();

builder.Services.AddScoped<UpdateDepartmentLocationsHadler>();

builder.Services.AddScoped<UpdateParentDepartmentHandler>();

builder.Services.AddScoped<GetLocationByIdHandle>();

builder.Services.AddScoped<GetLocationByDepartmentHandle>();

builder.Services.AddScoped<GetLocationsHandler>();

builder.Services.AddScoped<GetPositionsHandler>();

builder.Services.AddScoped<GetDepartmentByIdHandler>();

builder.Services.AddScoped<GetDepartmentByLocationHandler>();

builder.Services.AddScoped<GetDepartmentsTopByPositionsHandler>();

builder.Services.AddScoped<GetParentDepartmentsHandler>();

builder.Services.AddScoped<GetChildrenLazyHandler>();

builder.Services.AddScoped<SoftDeleteDepartmentHandler>();

builder.Services.AddStackExchangeRedisCache(setup =>
{
    setup.Configuration = builder.Configuration.GetConnectionString("Redis");
});

builder.Services.AddHybridCache(options => options.DefaultEntryOptions = new HybridCacheEntryOptions
{
    LocalCacheExpiration = TimeSpan.FromMinutes(5), Expiration = TimeSpan.FromMinutes(30),
});

var app = builder.Build();

app.UseRequestCorrelationId();
app.UseExceptionMiddleware();

app.UseSerilogRequestLogging();

app.UseHttpLogging();


// Configure the HTTP request pipeline.
// if (app.Environment.IsDevelopment() || app.Environment.Is)
app.MapOpenApi("/openapi/v1/swagger.json");

app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1/swagger.json", "DirectoryService"));

app.ConfigureCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGrpcService<DirectoryLookupService>();

app.Run();

namespace DirectoryService
{
    public partial class Program;
}