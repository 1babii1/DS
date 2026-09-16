using Shared.Outbox;

namespace SearchService.Web.Consumers;

public class DomainEventsConsumerOptions
{
    public string BootstrapServers { get; set; } = null!;

    public KafkaSecurityOptions Security { get; set; } = new(null, null);

    public string[] Topics { get; set; } = [];

    public string GroupId { get; set; } = "search-service";
}
