using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Shared.Outbox;

namespace Shared.UnitTests;

// Provisioning asks for several topics at once. Some may already exist (another instance, an earlier start); that is not
// a failure. Any other error is.
public class KafkaTopicProvisionerTests
{
    private static CreateTopicReport Report(ErrorCode code) => new() { Topic = "t", Error = code };

    [Fact]
    public void A_mix_of_created_and_already_existing_topics_is_fine()
    {
        Assert.True(KafkaTopicProvisioner.OnlyAlreadyExisted([Report(ErrorCode.TopicAlreadyExists), Report(ErrorCode.NoError)]));
    }

    [Fact]
    public void Only_existing_topics_is_fine()
    {
        Assert.True(KafkaTopicProvisioner.OnlyAlreadyExisted([Report(ErrorCode.TopicAlreadyExists), Report(ErrorCode.TopicAlreadyExists)]));
    }

    [Fact]
    public void Any_other_error_is_a_failure_even_beside_existing_topics()
    {
        Assert.False(KafkaTopicProvisioner.OnlyAlreadyExisted([Report(ErrorCode.TopicAlreadyExists), Report(ErrorCode.TopicAuthorizationFailed)]));
        Assert.False(KafkaTopicProvisioner.OnlyAlreadyExisted([Report(ErrorCode.Local_TimedOut)]));
    }
}
