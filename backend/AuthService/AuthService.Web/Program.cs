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
builder.Services.AddRazorPages();
builder.Services.AddOptions<WebClientOptions>()
    .Bind(builder.Configuration.GetSection(WebClientOptions.SectionName))
    .Validate(
        options => options.IsValid(builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Docker")),
        "Enabled web client requires a strong client secret and an HTTPS frontend origin (loopback HTTP is local-only).")
    .ValidateOnStart();
builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:3000")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

builder.Services.AddInfrastructurePostgres(builder.Configuration);

builder.Services.AddIdentityServices();
builder.Services.AddOpenIddictServer(builder.Environment, builder.Configuration);

var app = builder.Build();

app.UseSerilogRequestLogging();

app.MapOpenApi("/openapi/v1/swagger.json");
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1/swagger.json", "AuthService"));

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();

await OpenIddictSeeder.SeedAsync(app.Services);
await WebClientSeeder.SeedAsync(app.Services);

app.Run();

namespace AuthService.Web
{
    public partial class Program;
}
