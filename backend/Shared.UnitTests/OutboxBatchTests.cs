using Microsoft.Extensions.Logging.Abstractions;
using Shared.Outbox;

namespace Shared.UnitTests;

// The batch logic is separated from Kafka and the database precisely so these can run
// without either: what is being proven is which messages get marked processed, counted as
// failed, or parked - not that a broker is reachable.
public class OutboxBatchTests
{
    private const int MaxAttempts = 3;

    private static OutboxMessage NewMessage(string type = "T") => OutboxMessage.Create(type, "agg", "{}");

    private static Task<OutboxBatchResult> Run(
        IReadOnlyList<OutboxMessage> pending, Func<OutboxMessage, CancellationToken, Task> publish) =>
        OutboxBatch.ProcessAsync(pending, publish, MaxAttempts, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task A_failing_message_does_not_stop_healthy_ones_around_it()
    {
        var poison = NewMessage("poison");
        var healthy1 = NewMessage();
        var healthy2 = NewMessage();

        var result = await Run(
            [poison, healthy1, healthy2],
            (m, _) => m.Type == "poison" ? throw new InvalidOperationException("boom") : Task.CompletedTask);

        Assert.Equal(2, result.Succeeded);
        Assert.NotNull(healthy1.ProcessedAt);
        Assert.NotNull(healthy2.ProcessedAt);
        Assert.Null(poison.ProcessedAt);
        Assert.Equal(1, poison.AttemptCount);
        Assert.Equal("boom", poison.LastError);
        Assert.Null(poison.ParkedAt);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_beside_healthy_ones_is_parked_after_max_attempts()
    {
        var poison = NewMessage("poison");

        for (var i = 0; i < MaxAttempts; i++)
        {
            await Run(
                [poison, NewMessage()],
                (m, _) => m.Type == "poison" ? throw new InvalidOperationException("boom") : Task.CompletedTask);
        }

        Assert.Equal(MaxAttempts, poison.AttemptCount);
        Assert.NotNull(poison.ParkedAt);
        Assert.Null(poison.ProcessedAt);
    }

    [Fact]
    public async Task A_total_outage_counts_no_attempts_and_parks_nothing()
    {
        var messages = new[] { NewMessage(), NewMessage(), NewMessage() };

        for (var i = 0; i < MaxAttempts * 2; i++)
        {
            var result = await Run(messages, (_, _) => throw new InvalidOperationException("broker down"));
            Assert.Equal(0, result.Succeeded);
        }

        Assert.All(messages, m =>
        {
            Assert.Equal(0, m.AttemptCount);
            Assert.Null(m.ParkedAt);
            Assert.Null(m.ProcessedAt);
        });
    }

    [Fact]
    public async Task Cancellation_is_not_treated_as_a_message_failure()
    {
        var message = NewMessage();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() => OutboxBatch.ProcessAsync(
            [message, NewMessage()],
            (_, _) => throw new OperationCanceledException(),
            MaxAttempts,
            NullLogger.Instance,
            cts.Token));

        Assert.Equal(0, message.AttemptCount);
    }

    [Fact]
    public void Redrive_makes_a_parked_message_publishable_again()
    {
        var message = NewMessage();
        message.RecordFailure("boom", maxAttempts: 1);
        Assert.NotNull(message.ParkedAt);

        message.Redrive();

        Assert.Null(message.ParkedAt);
        Assert.Equal(0, message.AttemptCount);
        Assert.Null(message.ProcessedAt);
    }

    [Fact]
    public void The_recorded_error_is_truncated_so_one_huge_exception_cannot_break_the_save()
    {
        var message = NewMessage();

        message.RecordFailure(new string('x', 50_000), maxAttempts: 10);

        Assert.True(message.LastError!.Length <= OutboxMessage.MaxErrorLength);
    }
}
