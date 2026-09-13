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

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private static ConsumeResult<string, string> BuildResult(
        Guid messageId, string topic, string key, string messageType, string payload)
    {
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(messageId.ToString()) },
            { "message-type", Encoding.UTF8.GetBytes(messageType) },
        };

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
