using System.Text;
using System.Text.Json;
using EmployeeService.Application.Database;
using EmployeeService.Domain;
using EmployeeService.Infrastructure.Postgres;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Outbox;

namespace EmployeeService.Web.Consumers;

// The EmployeeService side of the choreographed "Hire Employee" saga: reacts to
// AuthService's outcome for the account it tried to provision, completing the
// employee's PendingProvisioning state or compensating it. Same shape as
// AuditConsumer/EmployeeEventsConsumer (AuthService) - retry, dead-letter,
// idempotent ProcessMessage.
public class AuthEventsConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<EmployeeConsumerOptions> options,
    ILogger<AuthEventsConsumer> logger) : BackgroundService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)];

    private readonly EmployeeConsumerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await KafkaTopicProvisioner.WaitForTopicsAsync(
            _options.BootstrapServers, _options.Security, logger, stoppingToken, _options.Topics);

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await Task.Run(() => Run(stoppingToken), stoppingToken);
    }

    private void Run(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };
        _options.Security.ApplyTo(config);

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(_options.Topics);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result;
            try
            {
                result = consumer.Consume(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                logger.LogError(ex, "Kafka consume error");
                Thread.Sleep(TimeSpan.FromSeconds(1));
                continue;
            }

            if (result?.Message is null)
            {
                continue;
            }

            if (HandleWithRetryAndDeadLetter(result, stoppingToken))
            {
                consumer.Commit(result);
            }
            else
            {
                consumer.Seek(result.TopicPartitionOffset);

                try
                {
                    Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        consumer.Close();
    }

    internal bool HandleWithRetryAndDeadLetter(ConsumeResult<string, string> result, CancellationToken stoppingToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                ProcessMessage(result);
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                logger.LogWarning(
                    ex,
                    "Failed to process auth event on {Topic} (attempt {Attempt}/{MaxAttempts})",
                    result.Topic,
                    attempt,
                    MaxAttempts);

                if (attempt < MaxAttempts)
                {
                    try
                    {
                        Task.Delay(RetryDelays[attempt - 1], stoppingToken).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }
            }
        }

        return TryDeadLetter(result, lastError!);
    }

    private bool TryDeadLetter(ConsumeResult<string, string> result, Exception error)
    {
        var messageId = GetHeader(result.Message.Headers, "message-id");
        if (messageId is null || !Guid.TryParse(messageId, out var messageGuid))
        {
            return true;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();

            if (dbContext.DeadLetters.Any(d => d.MessageId == messageGuid))
            {
                return true;
            }

            dbContext.DeadLetters.Add(DeadLetterEntry.Create(
                messageGuid,
                result.Topic,
                result.Message.Key,
                result.Message.Value,
                error.ToString(),
                MaxAttempts));

            dbContext.SaveChanges();

            logger.LogError(
                error,
                "Auth event {MessageId} on {Topic} exhausted retries and was moved to dead_letters",
                messageGuid,
                result.Topic);

            return true;
        }
        catch (DbUpdateException) when (DeadLetterAlreadyRecorded(messageGuid))
        {
            return true;
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "Failed to write dead letter for auth event {MessageId} on {Topic} - the database is likely down",
                messageGuid,
                result.Topic);
            return false;
        }
    }

    private bool DeadLetterAlreadyRecorded(Guid messageGuid)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();
        return dbContext.DeadLetters.Any(d => d.MessageId == messageGuid);
    }

    private void ProcessMessage(ConsumeResult<string, string> result)
    {
        var messageId = GetHeader(result.Message.Headers, "message-id");
        var messageType = GetHeader(result.Message.Headers, "message-type");

        if (messageId is null || !Guid.TryParse(messageId, out _))
        {
            logger.LogWarning("Skipping message without a valid message-id header on topic {Topic}", result.Topic);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IEmployeeRepository>();

        switch (messageType)
        {
            case AccountProvisionedEvent.MessageType:
            {
                var evt = JsonSerializer.Deserialize<AccountProvisionedEvent>(result.Message.Value)
                    ?? throw new InvalidOperationException($"Could not deserialize {AccountProvisionedEvent.MessageType} payload");
                CompleteProvisioning(repository, evt.EmployeeId);
                break;
            }

            case AccountProvisioningFailedEvent.MessageType:
            {
                var evt = JsonSerializer.Deserialize<AccountProvisioningFailedEvent>(result.Message.Value)
                    ?? throw new InvalidOperationException($"Could not deserialize {AccountProvisioningFailedEvent.MessageType} payload");
                FailProvisioning(repository, evt.EmployeeId, evt.Reason);
                break;
            }

            default:
                // Nothing else on auth.events concerns the employee's provisioning state.
                break;
        }
    }

    private static void CompleteProvisioning(IEmployeeRepository repository, Guid employeeId)
    {
        var employee = repository.GetById(employeeId, CancellationToken.None).GetAwaiter().GetResult();
        if (employee.IsFailure)
        {
            // The employee this event refers to doesn't exist - nothing to complete.
            return;
        }

        // Idempotent by construction: Employee.CompleteProvisioning() only ever
        // leaves PendingProvisioning, so redelivery of the same event is a no-op.
        employee.Value.CompleteProvisioning();
        repository.Save(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void FailProvisioning(IEmployeeRepository repository, Guid employeeId, string reason)
    {
        var employee = repository.GetById(employeeId, CancellationToken.None).GetAwaiter().GetResult();
        if (employee.IsFailure)
        {
            return;
        }

        employee.Value.FailProvisioning(reason);
        repository.Save(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string? GetHeader(Headers headers, string key)
    {
        if (!headers.TryGetLastBytes(key, out var bytes))
        {
            return null;
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
