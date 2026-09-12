namespace AuditService.Infrastructure;

public class AuditConsumerOptions
{
    public string BootstrapServers { get; set; } = null!;

    public string[] Topics { get; set; } = [];

    public string GroupId { get; set; } = "audit-service";
}
