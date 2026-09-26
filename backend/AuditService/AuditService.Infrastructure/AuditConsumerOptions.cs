using Shared.Kafka;

namespace AuditService.Infrastructure;

public class AuditConsumerOptions : KafkaConsumerOptions
{
    public AuditConsumerOptions() => GroupId = "audit-service";
}
