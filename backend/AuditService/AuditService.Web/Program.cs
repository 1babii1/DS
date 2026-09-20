using AuditService.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Serilog;
using Shared.HealthChecks;
using Shared.Middlewares;
using Shared.Observability;
using Shared.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Host.UseSerilog((context, _, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .AddOtlpLogging(context.Configuration, "audit-service"));

builder.Services.AddObservability(builder.Configuration, "audit-service");

builder.Services.AddControllers();
builder.Services.AddEnvelopeModelStateValidation();
builder.Services.AddOpenApi();

builder.Services.AddPlatformJwtAuthentication(builder.Configuration, builder.Environment);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("IsAdmin", policy => policy.RequireRole(RoleNames.Admin));

builder.Services.AddAuditInfrastructure(builder.Configuration);

builder.Services.AddDatabaseHealthCheck<AuditDbContext>();

var app = builder.Build();

app.UseRequestCorrelationId();
app.UseExceptionMiddleware();

app.UseSerilogRequestLogging();

app.MapOpenApi("/openapi/v1/swagger.json");
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1/swagger.json", "AuditService"));

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapDefaultHealthChecks();

app.Run();

namespace AuditService.Web
{
    public partial class Program;
}