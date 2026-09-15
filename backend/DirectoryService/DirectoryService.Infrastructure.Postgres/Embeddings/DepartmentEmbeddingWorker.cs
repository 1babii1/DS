using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DirectoryService.Infrastructure.Postgres.Embeddings;

// Best-effort background indexer: embeds active departments that don't have one yet.
// Not wired to the outbox - a missed or delayed embedding just means the department
// is temporarily absent from semantic search, not a correctness issue for the rest
// of the system, so a simple poll loop is enough (no re-embedding on name edits yet).
public sealed class DepartmentEmbeddingWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<EmbeddingsOptions> options,
    ILogger<DepartmentEmbeddingWorker> logger) : BackgroundService
{
    private readonly EmbeddingsOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EmbedPendingDepartmentsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Department embedding pass failed");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task EmbedPendingDepartmentsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();
        var embeddingClient = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();

        // Two independent queries filtered in memory: EF Core can't translate a correlated
        // subquery predicate over Departments.Id once it goes through the DepartmentId value
        // converter, so a single Where(!DepartmentEmbeddings.Any(...)) query fails to translate.
        var embeddedIds = (await db.DepartmentEmbeddings
            .Select(e => e.DepartmentId)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var pending = (await db.Departments
            .Where(d => d.IsActive)
            .OrderBy(d => d.CreatedAt)
            .Select(d => new { Id = d.Id.Value, Name = d.Name.Value, Identifier = d.Identifier.Value })
            .ToListAsync(cancellationToken))
            .Where(d => !embeddedIds.Contains(d.Id))
            .Take(_options.BatchSize)
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var department in pending)
        {
            var text = $"{department.Name} ({department.Identifier})";
            var vector = await embeddingClient.EmbedAsync(text, cancellationToken);

            db.DepartmentEmbeddings.Add(new DepartmentEmbedding
            {
                DepartmentId = department.Id,
                Embedding = vector,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Embedded {Count} department(s)", pending.Count);
    }
}