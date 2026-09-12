using DirectoryService.Application.Database;
using DirectoryService.Application.Department.Commands;
using DirectoryService.Application.Department.Queries;
using DirectoryService.Application.Location.Commands;
using DirectoryService.Application.Location.Queries;
using DirectoryService.Application.Position;
using DirectoryService.Infrastructure.Postgres;
using DirectoryService.Infrastructure.Postgres.Backgrounds;
using DirectoryService.Infrastructure.Postgres.Database;
using DirectoryService.Infrastructure.Postgres.Repositories.Departments;
using DirectoryService.Infrastructure.Postgres.Repositories.Locations;
using DirectoryService.Infrastructure.Postgres.Repositories.Positions;
using DirectoryService.Middleware;
using FluentValidation;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Serilog;
using Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();

builder.Logging.AddConsole();

builder.Logging.SetMinimumLevel(LogLevel.Debug);

builder.Host.UseSerilog((context, _, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

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

builder.Services.AddControllers();

builder.Services.AddHttpLogging();

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

builder.Services.AddScoped<ILocationsRepository, EfCoreLocationsRepository>();

// builder.Services.AddScoped<ILocationsRepository, NpgsqlLocationsRepository>();
builder.Services.AddScoped<IPositionRepository, EfCorePositionRepository>();

builder.Services.AddScoped<IDepartmentRepository, EfCoreDepartmentsRepository>();

// builder.Services.AddScoped<IReadDbContext, DirectoryServiceDbContext>();
builder.Services.AddScoped<CreateLocationHandle>();

builder.Services.AddScoped<CreatePositionHandle>();

builder.Services.AddScoped<CreateDepartmentHandler>();

builder.Services.AddScoped<UpdateDepartmentLocationsHadler>();

builder.Services.AddScoped<UpdateParentDepartmentHandler>();

builder.Services.AddScoped<GetLocationByIdHandle>();

builder.Services.AddScoped<GetLocationByDepartmentHandle>();

builder.Services.AddScoped<GetDepartmentByIdHandler>();

builder.Services.AddScoped<GetDepartmentByLocationHandler>();

builder.Services.AddScoped<GetDepartmentsTopByPositionsHandler>();

builder.Services.AddScoped<GetParentDepartmentsHandler>();

builder.Services.AddScoped<GetChildrenLazyHandler>();

builder.Services.AddScoped<SoftDeleteDepartmentHandler>();

builder.Services.AddStackExchangeRedisCache(setup =>
{
    setup.Configuration = "localhost:6379";
});

builder.Services.AddHybridCache(options => options.DefaultEntryOptions = new HybridCacheEntryOptions
{
    LocalCacheExpiration = TimeSpan.FromMinutes(5), Expiration = TimeSpan.FromMinutes(30),
});

var app = builder.Build();

app.UseSerilogRequestLogging();

app.UseHttpLogging();

app.UseMiddleware<ExeptionHandlingMiddleware>();

// Configure the HTTP request pipeline.
// if (app.Environment.IsDevelopment() || app.Environment.Is)
app.MapOpenApi("/openapi/v1/swagger.json");

app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1/swagger.json", "DirectoryService"));

app.UseCors();

app.MapControllers();

app.Run();

namespace DirectoryService
{
    public partial class Program;
}