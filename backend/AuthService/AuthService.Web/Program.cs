using Shared.Middlewares;
using Shared.Cors;
using AuthService.Application;
using AuthService.Infrastructure.Postgres;
using AuthService.Web.Configuration;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Host.UseSerilog((context, _, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddFrameworkCors(builder.Configuration);

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

builder.Services.AddInfrastructurePostgres(builder.Configuration);

builder.Services.AddIdentityServices();
builder.Services.AddOpenIddictServer(builder.Environment, builder.Configuration);

var app = builder.Build();

app.UseRequestCorrelationId();
app.UseExceptionMiddleware();

app.UseSerilogRequestLogging();

app.MapOpenApi("/openapi/v1/swagger.json");
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1/swagger.json", "AuthService"));

app.ConfigureCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

await OpenIddictSeeder.SeedAsync(app.Services);

app.Run();

namespace AuthService.Web
{
    public partial class Program;
}
