using Shared.Kafka;

namespace RewardsService.Infrastructure.Consumers;

public class WelcomeBonusConsumerOptions : KafkaConsumerOptions
{
    public WelcomeBonusConsumerOptions() => GroupId = "rewards-service";

    public decimal WelcomeBonusAmount { get; set; } = 100;
}
