using CSharpFunctionalExtensions;
using DirectoryService.Application.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared;

namespace DirectoryService.Infrastructure.Postgres.Backgrounds;

public class ClearDbOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a soft-deleted row is kept before it is purged. Stored as a duration,
    /// not an absolute date: an absolute date captured at startup stops moving, so a
    /// long-running service would keep purging against the cutoff it had on boot.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);
}

public class ClearDbOfDeletedEntities : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ClearDbOfDeletedEntities> _logger;
    private readonly TimeSpan _delay;
    private readonly TimeSpan _retention;

    public ClearDbOfDeletedEntities(IServiceProvider services, ILogger<ClearDbOfDeletedEntities> logger,
        IOptions<ClearDbOptions> options)
    {
        _services = services;
        _logger = logger;
        _delay = options.Value.Interval;
        _retention = options.Value.Retention;
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("я начал работу!");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var stats = await ClearDb(stoppingToken);
                if (!stats.IsFailure)
                {
                    _logger.LogInformation(
                        "Удалено подразделений: {stats}", stats.Value);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Ошибка очистки удаленных сущностей");
            }

            await Task.Delay(_delay, stoppingToken);
        }
    }

    private async Task<Result<int, Error>> ClearDb(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        await using var dbContext = scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();
        var transactionManager = scope.ServiceProvider.GetRequiredService<ITransactionManager>();
        var date = DateTime.UtcNow - _retention;

        var transactionScopeResult =
            await transactionManager.BeginTransactionAsync(cancellationToken);

        if (transactionScopeResult.IsFailure)
        {
            return transactionScopeResult.Error;
        }

        await using var transactionScope = transactionScopeResult.Value;

        // Очистка связей
        var deleteDepId = await dbContext.Departments
            .Where(d => !d.IsActive && d.DeletedAt < date)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        await dbContext.DepartmentLocations
            .Where(dl => deleteDepId.Contains(dl.DepartmentId))
            .ExecuteDeleteAsync(cancellationToken);

        await dbContext.DepartmentPositions
            .Where(dp => deleteDepId.Contains(dp.DepartmentId))
            .ExecuteDeleteAsync(cancellationToken);

        // Обновление путей и удаление подразделений
        // var sql = """
        //           WITH deleted_dep AS(
        //               SELECT departments.identifier as di, nlevel(identifier::ltree) as level
        //               FROM departments
        //               WHERE is_active = false AND deleted_at < @date
        //           ),
        //               updated_paths AS (
        //                   UPDATE departments
        //                   SET path = subpath(path, 0, dd.level) || subpath(path, dd.level + 1)
        //                   FROM deleted_dep dd
        //                   WHERE departments.path <@ dd.di
        //                     AND departments.path != dd.di
        //                   RETURNING 1
        //           )
        //           DELETE FROM departments
        //           WHERE identifier IN (SELECT identifier FROM deleted_dep);
        //           SELECT
        //               (SELECT count(*) FROM updated_paths) as updated,
        //               (SELECT count(*) FROM deleted_dep) as deleted;
        //           """;
        //
        // var result = await dbContext.Database.SqlQueryRaw<Stats>(sql: sql, new { date }).FirstAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE directory.departments 
            SET path = subpath(path, 0, nlevel(identifier::ltree)) || subpath(path, nlevel(identifier::ltree) + 1)
            WHERE path <@ (
                SELECT identifier::ltree FROM directory.departments d2 
                WHERE d2.is_active = false AND d2.deleted_at < {0}
                AND departments.path <@ d2.identifier::ltree
                LIMIT 1
            )::ltree
            """, date);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM directory.departments 
            WHERE is_active = false AND deleted_at < {0}
            """, date);

        var commitResult = await transactionScope.CommitAsync(cancellationToken);
        if (commitResult.IsFailure)
        {
            await transactionScope.RollbackAsync(cancellationToken);
            _logger.LogError("Failed to commit transaction");
            return commitResult.Error;
        }

        return Result.Success<int, Error>(deleteDepId.Count);
    }
}