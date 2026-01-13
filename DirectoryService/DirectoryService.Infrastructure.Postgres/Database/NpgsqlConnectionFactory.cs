using System.Data;
using DirectoryService.Application.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DirectoryService.Infrastructure.Postgres.Database;

public class NpgsqlConnectionFactory : IDisposable, IAsyncDisposable, IDbConnectionFactory
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlConnectionFactory(IConfiguration configuration)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(configuration.GetConnectionString("DirectoryServiceDb"));
        dataSourceBuilder
            .UseLoggerFactory(LoggerFactory); // Configure logging
        _dataSource = dataSourceBuilder.Build();
    }

    public async Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        return await _dataSource.OpenConnectionAsync(cancellationToken);
    }

    private ILoggerFactory LoggerFactory => Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole());

    public void Dispose() => _dataSource.Dispose();

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}