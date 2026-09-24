using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shared.Kafka;
using Shared.Outbox;

namespace Shared.Ops;

public record ParkedOutboxSummary(
    Guid Id, string Type, string AggregateId, int AttemptCount, string? LastError, DateTime OccurredAt, DateTime ParkedAt);

public record ParkedOutboxDetail(ParkedOutboxSummary Summary, string Payload);

public record DeadLetterSummary(
    Guid Id, Guid MessageId, string Topic, string MessageKey, string Error, int AttemptCount, DateTime FailedAt);

public record DeadLetterDetail(DeadLetterSummary Summary, string Payload);

public enum RedriveOutcome
{
    Redriven,
    NotFound,
    NotParked,
}

// Kept separate from the route mapping so the logic runs in tests against a real database
// without an HTTP pipeline, the same way the rest of this codebase tests handlers.
public static class OpsHandlers
{
    public const string RedrivenEventType = "OutboxMessageRedriven";

    public static async Task<IReadOnlyList<ParkedOutboxSummary>> ListParkedAsync(
        DbContext db, int limit, CancellationToken ct) =>
        await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.ParkedAt != null)
            .OrderBy(m => m.ParkedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(m => new ParkedOutboxSummary(
                m.Id, m.Type, m.AggregateId, m.AttemptCount, m.LastError, m.OccurredAt, m.ParkedAt!.Value))
            .ToListAsync(ct);

    public static async Task<ParkedOutboxDetail?> GetParkedAsync(DbContext db, Guid id, CancellationToken ct) =>
        await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Id == id && m.ParkedAt != null)
            .Select(m => new ParkedOutboxDetail(
                new ParkedOutboxSummary(
                    m.Id, m.Type, m.AggregateId, m.AttemptCount, m.LastError, m.OccurredAt, m.ParkedAt!.Value),
                m.Payload))
            .SingleOrDefaultAsync(ct);

    // Redrive is safe to repeat and safe against the original having half-succeeded: consumers
    // dedupe by message-id, and the message keeps its id.
    public static async Task<RedriveOutcome> RedriveAsync(
        DbContext db, Guid id, string actor, CancellationToken ct)
    {
        var message = await db.Set<OutboxMessage>().SingleOrDefaultAsync(m => m.Id == id, ct);
        if (message is null)
        {
            return RedriveOutcome.NotFound;
        }

        if (message.ParkedAt is null)
        {
            return RedriveOutcome.NotParked;
        }

        message.Redrive();

        // Recorded as an event on the service's own outbox, so AuditService (which consumes every
        // topic) shows who put this message back, in the same transaction as the redrive itself.
        db.Set<OutboxMessage>().Add(OutboxMessage.Create(
            RedrivenEventType,
            message.AggregateId,
            JsonSerializer.Serialize(new { MessageId = message.Id, MessageType = message.Type, RedrivenBy = actor })));

        await db.SaveChangesAsync(ct);
        return RedriveOutcome.Redriven;
    }

    public static async Task<IReadOnlyList<DeadLetterSummary>> ListDeadLettersAsync<TContext>(
        TContext db, int limit, CancellationToken ct)
        where TContext : DbContext, IHasDeadLetters =>
        await db.DeadLetters.AsNoTracking()
            .OrderByDescending(d => d.FailedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(d => new DeadLetterSummary(
                d.Id, d.MessageId, d.Topic, d.MessageKey, d.Error, d.AttemptCount, d.FailedAt))
            .ToListAsync(ct);

    public static async Task<DeadLetterDetail?> GetDeadLetterAsync<TContext>(
        TContext db, Guid id, CancellationToken ct)
        where TContext : DbContext, IHasDeadLetters =>
        await db.DeadLetters.AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => new DeadLetterDetail(
                new DeadLetterSummary(d.Id, d.MessageId, d.Topic, d.MessageKey, d.Error, d.AttemptCount, d.FailedAt),
                d.Payload))
            .SingleOrDefaultAsync(ct);
}
