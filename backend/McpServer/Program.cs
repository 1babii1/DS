using McpServer.Embeddings;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Npgsql;

Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<EmbeddingsOptions>()
    .Bind(builder.Configuration.GetSection(EmbeddingsOptions.SectionName));

builder.Services.AddSingleton(_ =>
{
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("PlatformDb"));
    dataSourceBuilder.UseVector();
    return dataSourceBuilder.Build();
});

builder.Services.AddHttpClient<OllamaEmbeddingClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<EmbeddingsOptions>>().Value;
    client.BaseAddress = new Uri(options.OllamaBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Инструменты отдают данные сразу нескольких сервисов, включая ФИО и email
// сотрудников, в обход правил доступа, которые эти сервисы проверяют у себя.
// Поэтому те же токены и тот же JWKS, что и везде: без аутентификации этот
// endpoint был самым коротким путём к персональным данным во всей системе.
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

builder.Services.AddAuthorization();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapMcp("/mcp").RequireAuthorization();

app.Run();
