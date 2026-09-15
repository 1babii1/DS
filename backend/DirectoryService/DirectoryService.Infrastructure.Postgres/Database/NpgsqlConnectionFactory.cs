using System.Data;
using DirectoryService.Application.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DirectoryService.Infrastructure.Postgres.Database;

public class NpgsqlConnectionFactory : IDisposable, IAsyncDisposable, IDbConnectionFactory
{
    private readonly NpgsqlDataSource _dataSource;

    // Фабрика логгеров берётся из DI, а не создаётся здесь: LoggerFactory.Create
    // возвращает IDisposable, владеющий провайдерами и потоком консольного вывода,
    // и каждый вызов создавал новый, который никто не освобождал.
    public NpgsqlConnectionFactory(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(configuration.GetConnectionString("DirectoryServiceDb"));
        dataSourceBuilder.UseLoggerFactory(loggerFactory);
        _dataSource = dataSourceBuilder.Build();
    }

    public async Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        return await _dataSource.OpenConnectionAsync(cancellationToken);
    }

    public void Dispose() => _dataSource.Dispose();

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}