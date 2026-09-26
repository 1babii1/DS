using System.Text.Json;
using Confluent.Kafka;
using EmployeeService.Application.Database;
using EmployeeService.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Kafka;

namespace EmployeeService.Web.Consumers;

// The employee side of the hire -> provision-account saga: AuthService reports whether the
// login account was created, and the employee's provisioning state is completed or
// compensated accordingly. Consume loop, retries and dead-lettering come from
// KafkaRetryConsumer.
public class AuthEventsConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<EmployeeConsumerOptions> options,
    ILogger<AuthEventsConsumer> logger)
    : KafkaRetryConsumer<EmployeeDbContext>(scopeFactory, options.Value, logger)
{
    protected override string MessageKind => "employee";

    protected override Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var messageId = GetHeader(result.Message.Headers, "message-id");
        var messageType = GetHeader(result.Message.Headers, "message-type");

        if (messageId is null || !Guid.TryParse(messageId, out _))
        {
            Logger.LogWarning("Skipping message without a valid message-id header on topic {Topic}", result.Topic);
            return Task.CompletedTask;
        }

        using var scope = ScopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IEmployeeRepository>();

        switch (messageType)
        {
            case AccountProvisionedEvent.MessageType:
            {
                var evt = JsonSerializer.Deserialize<AccountProvisionedEvent>(result.Message.Value)
                    ?? throw new InvalidOperationException($"Could not deserialize {AccountProvisionedEvent.MessageType} payload");
                CompleteProvisioning(repository, evt.EmployeeId, cancellationToken);
                break;
            }

            case AccountProvisioningFailedEvent.MessageType:
            {
                var evt = JsonSerializer.Deserialize<AccountProvisioningFailedEvent>(result.Message.Value)
                    ?? throw new InvalidOperationException($"Could not deserialize {AccountProvisioningFailedEvent.MessageType} payload");
                FailProvisioning(repository, evt.EmployeeId, evt.Reason, cancellationToken);
                break;
            }

            default:
                // Nothing else on auth.events concerns the employee's provisioning state.
                break;
        }

        return Task.CompletedTask;
    }

    private static void CompleteProvisioning(
        IEmployeeRepository repository, Guid employeeId, CancellationToken cancellationToken)
    {
        var employee = repository.GetById(employeeId, cancellationToken).GetAwaiter().GetResult();
        if (employee.IsFailure)
        {
            // The employee this event refers to doesn't exist - nothing to complete.
            return;
        }

        // Idempotent by construction: Employee.CompleteProvisioning() only ever
        // leaves PendingProvisioning, so redelivery of the same event is a no-op.
        employee.Value.CompleteProvisioning();
        repository.Save(cancellationToken).GetAwaiter().GetResult();
    }

    private static void FailProvisioning(
        IEmployeeRepository repository, Guid employeeId, string reason, CancellationToken cancellationToken)
    {
        var employee = repository.GetById(employeeId, cancellationToken).GetAwaiter().GetResult();
        if (employee.IsFailure)
        {
            return;
        }

        employee.Value.FailProvisioning(reason);
        repository.Save(cancellationToken).GetAwaiter().GetResult();
    }
}
