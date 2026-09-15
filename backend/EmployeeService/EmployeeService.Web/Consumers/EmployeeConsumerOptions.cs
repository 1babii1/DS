using Shared.Outbox;

namespace EmployeeService.Web.Consumers;

public class EmployeeConsumerOptions
{
    public string BootstrapServers { get; set; } = null!;

    public KafkaSecurityOptions Security { get; set; } = new(null, null);

    public string[] Topics { get; set; } = [];

    public string GroupId { get; set; } = "employee-service";
}
