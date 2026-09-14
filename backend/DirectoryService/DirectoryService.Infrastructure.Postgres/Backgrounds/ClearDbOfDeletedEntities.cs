using System.Runtime.CompilerServices;
using CSharpFunctionalExtensions;
using DirectoryService.Application.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared;

[assembly: InternalsVisibleTo("DirectoryService.IntegrationTests")]

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
        _logger.LogInformation("Deleted-department purge worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var stats = await ClearDb(stoppingToken);
                if (stats.IsSuccess)
                {
                    _logger.LogInformation("Purged {Count} deleted department(s)", stats.Value);
                }
                else
                {
                    _logger.LogError("Purge cycle failed: {Error}", stats.Error.GetMessage());
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Purge cycle threw an unhandled exception");
            }

            await Task.Delay(_delay, stoppingToken);
        }
    }

    internal async Task<Result<int, Error>> ClearDb(CancellationToken cancellationToken)
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

        // Promote every surviving descendant of a to-be-purged department one level up
        // before deleting it, so no row is left with a path/parent_id pointing at a
        // department that's about to disappear. Matches each descendant against the
        // deepest still-unprocessed purged ancestor above it (there can be more than one
        // when a whole subtree was soft-deleted together) and repeats until nothing
        // matches, so a multi-level deleted chain collapses fully in one run instead of
        // needing one scheduled run per level. Deepest first, not shallowest: a purged
        // ancestor's own stored path/depth never changes (it's excluded from promotion,
        // being inactive itself), so once a shallower ancestor's label has already been
        // stripped from a descendant's path, a deeper ancestor's reconstructed original
        // path - built from that same now-stale prefix - would stop matching at all.
        // Peeling from the bottom up keeps every not-yet-processed ancestor's prefix
        // untouched until it's its turn.
        //
        // d2.depth is the purged department's own 0-based position, i.e. the number of
        // labels before its own in path - identifier alone can't give that (it's always
        // a single label, so nlevel(identifier::ltree) is always 1 regardless of depth).
        // Since Delete() only rewrites the purged row's own last label (to "deleted-..."),
        // its prefix up to that label is untouched, so subpath(path, 0, depth) || identifier
        // reconstructs its pre-delete path - what descendants still hold on their own path.
        int promoted;
        do
        {
            promoted = await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE directory.departments AS descendant
                SET
                    path = subpath(descendant.path, 0, purged.depth) || subpath(descendant.path, purged.depth + 1),
                    depth = descendant.depth - 1,
                    parent_id = CASE WHEN descendant.parent_id = purged.id THEN purged.parent_id ELSE descendant.parent_id END
                FROM directory.departments AS purged
                WHERE descendant.is_active = true
                    AND purged.id = (
                        SELECT dd.id
                        FROM directory.departments dd
                        WHERE dd.is_active = false AND dd.deleted_at < {0}
                            AND descendant.path <@ (subpath(dd.path, 0, dd.depth) || dd.identifier::ltree)
                        ORDER BY dd.depth DESC
                        LIMIT 1
                    )
                """, [date], cancellationToken);
        }
        while (promoted > 0);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM directory.departments
            WHERE is_active = false AND deleted_at < {0}
            """, [date], cancellationToken);

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