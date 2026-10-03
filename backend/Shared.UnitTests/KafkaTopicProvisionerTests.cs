using Microsoft.Extensions.Configuration;
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

    [Fact]
    public void A_single_broker_gets_one_replica_and_no_minimum_it_could_not_meet()
    {
        var spec = KafkaTopicProvisioner.SpecificationFor("t", new KafkaSecurityOptions(null, null));

        Assert.Equal(1, spec.ReplicationFactor);
        Assert.False(spec.Configs.ContainsKey("min.insync.replicas"));
    }

    [Fact]
    public void A_cluster_setting_gives_the_topic_three_replicas_and_a_minimum_of_two_in_sync()
    {
        var spec = KafkaTopicProvisioner.SpecificationFor(
            "t", new KafkaSecurityOptions(null, null) { TopicReplicationFactor = 3, TopicMinInsyncReplicas = 2 });

        Assert.Equal(3, spec.ReplicationFactor);
        Assert.Equal("2", spec.Configs["min.insync.replicas"]);
    }

    [Fact]
    public void The_cluster_settings_are_read_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kafka:TopicReplicationFactor"] = "3", ["Kafka:TopicMinInsyncReplicas"] = "2" })
            .Build();

        var options = KafkaSecurityOptions.FromConfiguration(configuration);

        Assert.Equal(3, options.TopicReplicationFactor);
        Assert.Equal(2, options.TopicMinInsyncReplicas);
        Assert.Equal(1, KafkaSecurityOptions.FromConfiguration(new ConfigurationBuilder().Build()).TopicReplicationFactor);
    }

    [Fact]
    public void The_outbox_producer_waits_for_every_in_sync_replica_and_cannot_duplicate_a_retry()
    {
        var config = KafkaProducerSettings.For(new OutboxPublisherOptions { BootstrapServers = "b:9092" });

        Assert.Equal(Acks.All, config.Acks);
        Assert.True(config.EnableIdempotence);
    }
}
