using McpServer.Embeddings;
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

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp("/mcp");

app.Run();
