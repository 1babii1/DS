using Shared.Kafka;

namespace AuthService.Web.Consumers;

public class AuthConsumerOptions : KafkaConsumerOptions
{
    public AuthConsumerOptions() => GroupId = "auth-service";
}
