using EmployeeService.Application.Database;
using EmployeeService.Application.Employees.Commands;
using EmployeeService.Application.Employees.Queries;
using EmployeeService.Infrastructure.DirectoryGrpc;
using EmployeeService.Infrastructure.Postgres;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Serilog;
using Shared.Cors;
using Shared.HealthChecks;
using Shared.Middlewares;
using Shared.Observability;
using Shared.Outbox;
using Shared.Security;

// Required for the gRPC client to call DirectoryService over cleartext HTTP/2 (h2c) -
// no TLS between services inside the docker network for this pet project.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Host.UseSerilog((context, _, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .AddOtlpLogging(context.Configuration, "employee-service"));

builder.Services.AddObservability(builder.Configuration, "employee-service");

builder.Services.AddControllers();
builder.Services.AddEnvelopeModelStateValidation();
builder.Services.AddOpenApi();

builder.Services.AddFrameworkCors(builder.Configuration);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.MetadataAddress = builder.Configuration["Auth:MetadataAddress"];
        options.RequireHttpsMetadata = builder.Environment.IsProduction();
        options.TokenValidationParameters.ValidIssuer = builder.Configuration["Auth:Issuer"];
        options.TokenValidationParameters.ValidAudience = builder.Configuration["Auth:Audience"];
        options.TokenValidationParameters.RoleClaimType = "role";
        options.TokenValidationParameters.NameClaimType = "name";
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("CanEdit", policy => policy.RequireRole(RoleNames.Admin, RoleNames.Editor));

builder.Services.AddInfrastructurePostgres(builder.Configuration);
builder.Services.AddDirectoryGrpcClient(builder.Configuration);

builder.Services.AddScoped<IOutboxWriter, OutboxWriter>();
builder.Services.AddOutboxPublisher<EmployeeDbContext>(builder.Configuration, "employee.events");

builder.Services.AddScoped<HireEmployeeHandler>();
builder.Services.AddScoped<TransferEmployeeHandler>();
builder.Services.AddScoped<TerminateEmployeeHandler>();
builder.Services.AddScoped<GetEmployeeByIdHandler>();
builder.Services.AddScoped<ListEmployeesHandler>();

builder.Services.AddDatabaseHealthCheck<EmployeeDbContext>();

var app = builder.Build();

app.UseRequestCorrelationId();
app.UseExceptionMiddleware();

app.UseSerilogRequestLogging();

app.MapOpenApi("/openapi/v1/swagger.json");
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1/swagger.json", "EmployeeService"));

app.ConfigureCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapDefaultHealthChecks();

app.Run();

namespace EmployeeService.Web
{
    public partial class Program;
}