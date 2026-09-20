using Shared.Kafka;

namespace NotificationService.Web.Consumers;

public class DomainEventsConsumerOptions : KafkaConsumerOptions
{
    public DomainEventsConsumerOptions() => GroupId = "notification-service";
}
