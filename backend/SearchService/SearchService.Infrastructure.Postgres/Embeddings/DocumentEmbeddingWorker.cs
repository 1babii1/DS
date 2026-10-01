using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SearchService.Infrastructure.Postgres.Embeddings;

// Completes the rows the consumer staged. Best effort by design: a failed or missing model leaves rows without a
// vector (they are simply absent from semantic results, keyword search is unaffected) and the next pass tries again.
public sealed class DocumentEmbeddingWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<EmbeddingsOptions> options,
    ILogger<DocumentEmbeddingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await EmbedPendingAsync(stoppingToken);

            try
            {
                await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // One pass. Never throws for a model or database failure: it logs and leaves the rows for the next pass.
    public async Task<int> EmbedPendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SearchDbContext>();
            var client = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();

            var pending = await db.DocumentEmbeddings
                .Where(e => e.Embedding == null)
                .OrderBy(e => e.UpdatedAt)
                .Take(options.Value.BatchSize)
                .ToListAsync(cancellationToken);

            var done = 0;
            foreach (var row in pending)
            {
                var hashBefore = row.TextHash;
                var vector = await client.EmbedAsync(row.Text, cancellationToken);

                // The consumer may have changed this row's text while the model was busy; the vector belongs to the
                // text it was made from, so it is only stored if that is still the current text.
                var current = await db.DocumentEmbeddings.AsNoTracking()
                    .Where(e => e.DocumentId == row.DocumentId)
                    .Select(e => e.TextHash)
                    .SingleOrDefaultAsync(cancellationToken);
                if (current != hashBefore)
                {
                    continue;
                }

                row.Embedding = vector;
                await db.SaveChangesAsync(cancellationToken);
                done++;
            }

            if (done > 0)
            {
                logger.LogInformation("Embedded {Count} search document(s)", done);
            }

            return done;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Embedding pass failed; the rows stay pending for the next pass");
            return 0;
        }
    }
}
