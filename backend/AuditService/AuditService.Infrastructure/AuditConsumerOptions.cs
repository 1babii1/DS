using Shared.Outbox;

namespace AuditService.Infrastructure;

public class AuditConsumerOptions
{
    public string BootstrapServers { get; set; } = null!;

    public KafkaSecurityOptions Security { get; set; } = new(null, null);

    public string[] Topics { get; set; } = [];

    public string GroupId { get; set; } = "audit-service";
}