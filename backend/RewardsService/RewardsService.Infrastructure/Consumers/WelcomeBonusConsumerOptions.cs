using Shared.Outbox;

namespace RewardsService.Infrastructure.Consumers;

public class WelcomeBonusConsumerOptions
{
    public string BootstrapServers { get; set; } = null!;

    public KafkaSecurityOptions Security { get; set; } = new(null, null);

    public string[] Topics { get; set; } = [];

    public string GroupId { get; set; } = "rewards-service";

    public decimal WelcomeBonusAmount { get; set; } = 100;
}
