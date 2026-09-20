using Shared.Kafka;

namespace SearchService.Web.Consumers;

public class DomainEventsConsumerOptions : KafkaConsumerOptions
{
    public DomainEventsConsumerOptions() => GroupId = "search-service";
}
