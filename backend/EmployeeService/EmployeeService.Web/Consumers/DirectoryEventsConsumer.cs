using System.Text.Json;
using Confluent.Kafka;
using EmployeeService.Application.Authorization;
using EmployeeService.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Kafka;

namespace EmployeeService.Web.Consumers;

public class DirectoryConsumerOptions : KafkaConsumerOptions
{
    public DirectoryConsumerOptions()
    {
        GroupId = "employee-department-tree";
        Topics = ["directory.events.v2"];
    }
}

/// <summary>
/// Keeps the authorization store's tree of departments in step with DirectoryService's (ADR 0057). Only runs in tree mode. Events for one department
/// arrive in order (the department is the key), every handling is idempotent and atomic, and a failure is retried and then dead-lettered by the base class.
/// </summary>
public class DirectoryEventsConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<DirectoryConsumerOptions> options,
    ILogger<DirectoryEventsConsumer> logger,
    Shared.Avro.IEventAvroDecoder? avro = null)
    : KafkaRetryConsumer<EmployeeDbContext>(scopeFactory, options.Value, logger, avro)
{
    protected override string MessageKind => "employee-department-tree";

    protected override async Task ProcessMessageAsync(ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var messageType = GetHeader(result.Message.Headers, "message-type");
        if (messageType is not (DepartmentCreatedEvent.MessageType or DepartmentMovedEvent.MessageType or DepartmentDeletedEvent.MessageType))
        {
            // The renames, the positions, the locations: nothing about where a department sits.
            return;
        }

        using var scope = ScopeFactory.CreateScope();
        var tree = scope.ServiceProvider.GetRequiredService<DepartmentTreeSync>();

        switch (messageType)
        {
            case DepartmentCreatedEvent.MessageType:
            {
                var evt = Read<DepartmentCreatedEvent>(result);
                await tree.OnCreated(evt.DepartmentId, evt.ParentDepartmentId, cancellationToken);
                break;
            }

            case DepartmentMovedEvent.MessageType:
            {
                var evt = Read<DepartmentMovedEvent>(result);
                await tree.OnMoved(evt.DepartmentId, evt.NewParentDepartmentId, cancellationToken);
                break;
            }

            case DepartmentDeletedEvent.MessageType:
            {
                var evt = Read<DepartmentDeletedEvent>(result);
                await tree.OnDeleted(evt.DepartmentId, cancellationToken);
                break;
            }
        }
    }

    private static T Read<T>(ConsumeResult<string, string> result) =>
        JsonSerializer.Deserialize<T>(result.Message.Value)
        ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name} payload");
}
