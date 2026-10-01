using Confluent.Kafka;
using DirectoryService.Application.IntegrationEvents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Kafka;
using System.Text.Json;

namespace DirectoryService.Infrastructure.Postgres.Embeddings;

public sealed class DepartmentRenamedEmbeddingConsumerOptions : KafkaConsumerOptions
{
    public DepartmentRenamedEmbeddingConsumerOptions() => GroupId = "directory-embeddings";
}

// Reads this service's own directory.events. When a department is renamed its embedding, made from the old name,
// is dropped; DepartmentEmbeddingWorker then embeds the new name like any department without one. The consumer
// never calls the embedding model, so a slow or absent model cannot stall the topic or fill the dead-letter table:
// until the worker catches up the department is simply missing from semantic search, as a new one is.
public sealed class DepartmentRenamedEmbeddingConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<DepartmentRenamedEmbeddingConsumerOptions> options,
    ILogger<DepartmentRenamedEmbeddingConsumer> logger)
    : KafkaRetryConsumer<DirectoryServiceDbContext>(scopeFactory, options.Value, logger)
{
    protected override string MessageKind => "directory-embeddings";

    protected override async Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        if (GetHeader(result.Message.Headers, "message-type") != DepartmentEventTypes.Renamed)
        {
            return;
        }

        var renamed = JsonSerializer.Deserialize<DepartmentRenamedEvent>(result.Message.Value)
            ?? throw new InvalidOperationException($"Could not deserialize {DepartmentEventTypes.Renamed} payload");

        using var scope = ScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();

        // Idempotent: redelivery deletes nothing the second time.
        await db.DepartmentEmbeddings
            .Where(e => e.DepartmentId == renamed.DepartmentId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
