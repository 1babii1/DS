using System.Text;
using AuditService.Infrastructure;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuditService.IntegrationTests;

public class AuditConsumerTests : IClassFixture<AuditTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly AuditConsumer _sut;

    public AuditConsumerTests(AuditTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;

        _sut = new AuditConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuditConsumerOptions()),
            _services.GetRequiredService<ILogger<AuditConsumer>>());
    }

    [Fact]
    public async Task Message_that_processes_successfully_is_recorded_once()
    {
        var messageId = Guid.NewGuid();
        var result = BuildResult(messageId, "directory.events", "dep-1", "DepartmentCreated", "{}");

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);
        var entry = await ExecuteInDb(db => db.Entries.SingleAsync(e => e.MessageId == messageId));
        Assert.Equal("directory", entry.SourceService);
        Assert.Equal("DepartmentCreated", entry.EventType);
    }

    // The time an entry sits at is when the event happened, as the producer says, not when this consumer got to it:
    // an outage or a replay must not move history.
    [Fact]
    public async Task An_entry_is_placed_at_the_producers_event_time_not_at_the_time_it_was_received()
    {
        var messageId = Guid.NewGuid();
        var happened = new DateTime(2026, 3, 5, 10, 15, 30, DateTimeKind.Utc);
        var result = BuildResult(
            messageId, "directory.events", "dep-1", "DepartmentCreated", "{}", happened.ToString("O"));

        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));

        var entry = await ExecuteInDb(db => db.Entries.SingleAsync(e => e.MessageId == messageId));
        Assert.Equal(happened, entry.OccurredAt);
        Assert.True(entry.ReceivedAt > happened.AddDays(30));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("yesterday")]
    [InlineData("")]
    public async Task Without_a_usable_event_time_the_entry_falls_back_to_the_time_it_was_received(string? header)
    {
        var messageId = Guid.NewGuid();
        var before = DateTime.UtcNow.AddSeconds(-1);
        var result = BuildResult(messageId, "directory.events", "dep-1", "DepartmentCreated", "{}", header);

        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));

        var entry = await ExecuteInDb(db => db.Entries.SingleAsync(e => e.MessageId == messageId));
        Assert.InRange(entry.OccurredAt, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Redelivering_the_same_message_id_does_not_duplicate_it()
    {
        var messageId = Guid.NewGuid();
        var result = BuildResult(messageId, "directory.events", "dep-1", "DepartmentCreated", "{}");

        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));
        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));

        var count = await ExecuteInDb(db => db.Entries.CountAsync(e => e.MessageId == messageId));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Message_without_a_valid_message_id_header_is_skipped_without_error()
    {
        var result = new ConsumeResult<string, string>
        {
            Topic = "directory.events",
            Message = new Message<string, string> { Key = "dep-1", Value = "{}", Headers = new Headers() },
        };

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);
        var count = await ExecuteInDb(db => db.Entries.CountAsync());
        Assert.Equal(0, count);
    }

    /// <summary>
    /// Same fault this session proved live against a real Kafka broker with a real
    /// poison message: a key longer than the AggregateId column's varchar(200) makes
    /// every processing attempt fail deterministically, the same way a genuinely
    /// malformed event would. After exhausting retries the message must be parked in
    /// dead_letters with its full, un-truncated payload - not silently dropped.
    /// </summary>
    [Fact]
    public async Task Message_that_always_fails_processing_ends_up_in_dead_letters_after_max_attempts()
    {
        var messageId = Guid.NewGuid();
        var poisonKey = new string('x', 400);
        var result = BuildResult(messageId, "directory.events", poisonKey, "DepartmentCreated", "{}");

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);

        var entryCount = await ExecuteInDb(db => db.Entries.CountAsync(e => e.MessageId == messageId));
        Assert.Equal(0, entryCount);

        var deadLetter = await ExecuteInDb(db => db.DeadLetters.SingleAsync(d => d.MessageId == messageId));
        Assert.Equal(3, deadLetter.AttemptCount);
        Assert.Equal(poisonKey, deadLetter.MessageKey);
        Assert.Equal("directory.events", deadLetter.Topic);
    }

    [Fact]
    public async Task Redelivering_a_dead_lettered_message_does_not_duplicate_the_dead_letter()
    {
        var messageId = Guid.NewGuid();
        var poisonKey = new string('x', 400);
        var result = BuildResult(messageId, "directory.events", poisonKey, "DepartmentCreated", "{}");

        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));
        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));

        var count = await ExecuteInDb(db => db.DeadLetters.CountAsync(d => d.MessageId == messageId));
        Assert.Equal(1, count);
    }

    // The Any() check before the insert is only a fast path; what makes a concurrent redelivery harmless is the primary key on
    // recorded_messages (ADR 0027, ADR 0028). Sixteen consumers racing on one message must leave exactly one entry.
    [Fact]
    public async Task The_same_message_delivered_concurrently_leaves_exactly_one_entry()
    {
        var messageId = Guid.NewGuid();
        var payload = """{"DepartmentId":"3f2c1c1e-0000-4000-8000-000000000001"}""";

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            _sut.HandleWithRetryAndDeadLetter(
                BuildResult(messageId, "directory.events", "dep-1", "DepartmentCreated", payload), CancellationToken.None))));

        Assert.All(results, Assert.True);
        Assert.Equal(1, await ExecuteInDb(db => db.Entries.CountAsync(e => e.MessageId == messageId)));
        Assert.Equal(1, await ExecuteInDb(db => db.RecordedMessages.CountAsync(m => m.MessageId == messageId)));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private static ConsumeResult<string, string> BuildResult(
        Guid messageId, string topic, string key, string messageType, string payload, string? occurredAtHeader = null)
    {
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(messageId.ToString()) },
            { "message-type", Encoding.UTF8.GetBytes(messageType) },
        };
        if (occurredAtHeader is not null)
        {
            headers.Add("occurred-at", Encoding.UTF8.GetBytes(occurredAtHeader));
        }

        return new ConsumeResult<string, string>
        {
            Topic = topic,
            Message = new Message<string, string> { Key = key, Value = payload, Headers = headers },
        };
    }

    private async Task<T> ExecuteInDb<T>(Func<AuditDbContext, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        return await action(db);
    }
}