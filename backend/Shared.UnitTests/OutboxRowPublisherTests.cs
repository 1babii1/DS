using Shared.Avro;
using Shared.Outbox;

namespace Shared.UnitTests;

// While an event goes to two topics, what matters is which side is marked done after which outcome: a failure on one
// side must never make the other side repeat, and nothing may be marked that was not sent.
public class OutboxRowPublisherTests
{
    private const string AvroTopic = "x.events.v2";

    private sealed class FakeEncoder(bool configured = true) : IEventAvroEncoder
    {
        public bool IsConfigured => configured;

        public bool IsReady => true;

        public int JsonEncodes { get; private set; }

        public bool Fail { get; set; }

        public bool TryEncode(string eventType, object payload, out byte[] bytes)
        {
            bytes = [];
            return false;
        }

        public Task<byte[]> EncodeJsonAsync(string eventType, string json, CancellationToken cancellationToken)
        {
            JsonEncodes++;
            return Fail ? throw new InvalidOperationException("registry down") : Task.FromResult(new byte[] { 9, 9 });
        }
    }

    private sealed class Sends
    {
        public int Json { get; private set; }

        public List<byte[]> Avro { get; } = [];

        public bool FailAvro { get; set; }

        public Task SendJson(OutboxMessage m, CancellationToken ct)
        {
            Json++;
            return Task.CompletedTask;
        }

        public Task SendAvro(OutboxMessage m, byte[] bytes, CancellationToken ct)
        {
            if (FailAvro)
            {
                throw new InvalidOperationException("broker said no");
            }

            Avro.Add(bytes);
            return Task.CompletedTask;
        }
    }

    private static OutboxMessage Row(bool expectAvro, byte[]? bytes = null)
    {
        var row = OutboxMessage.Create("EmployeeHired", "agg", "{\"EmployeeId\":\"x\"}");
        if (expectAvro)
        {
            row.ExpectAvro(bytes);
        }

        return row;
    }

    private static Task Publish(OutboxMessage row, Sends sends, IEventAvroEncoder? encoder, string? topic = AvroTopic) =>
        OutboxRowPublisher.PublishAsync(row, sends.SendJson, sends.SendAvro, encoder, topic, CancellationToken.None);

    [Fact]
    public async Task A_row_with_its_avro_bytes_goes_to_both_topics_and_both_sides_are_marked()
    {
        var sends = new Sends();
        var row = Row(true, [1, 2, 3]);

        await Publish(row, sends, new FakeEncoder());

        Assert.Equal(1, sends.Json);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(sends.Avro));
        Assert.NotNull(row.ProcessedAt);
        Assert.NotNull(row.AvroPublishedAt);
    }

    [Fact]
    public async Task A_row_written_while_the_registry_was_down_is_encoded_from_its_json_at_publish_time()
    {
        var sends = new Sends();
        var encoder = new FakeEncoder();
        var row = Row(true, bytes: null);

        await Publish(row, sends, encoder);

        Assert.Equal(1, encoder.JsonEncodes);
        Assert.Equal(new byte[] { 9, 9 }, Assert.Single(sends.Avro));
        Assert.NotNull(row.AvroPublishedAt);
    }

    [Fact]
    public async Task When_the_avro_side_fails_the_json_side_stays_done_and_is_never_sent_again()
    {
        var sends = new Sends { FailAvro = true };
        var row = Row(true, [1]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Publish(row, sends, new FakeEncoder()));

        Assert.NotNull(row.ProcessedAt);
        Assert.Null(row.AvroPublishedAt);

        sends.FailAvro = false;
        await Publish(row, sends, new FakeEncoder());

        Assert.Equal(1, sends.Json);
        Assert.Single(sends.Avro);
        Assert.NotNull(row.AvroPublishedAt);
    }

    [Fact]
    public async Task A_registry_that_is_down_leaves_the_avro_side_undone_without_losing_the_json_side()
    {
        var sends = new Sends();
        var encoder = new FakeEncoder { Fail = true };
        var row = Row(true, bytes: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Publish(row, sends, encoder));

        Assert.Equal(1, sends.Json);
        Assert.Empty(sends.Avro);
        Assert.NotNull(row.ProcessedAt);
        Assert.Null(row.AvroPublishedAt);
    }

    [Fact]
    public async Task A_row_written_before_avro_existed_is_never_sent_to_the_avro_topic()
    {
        var sends = new Sends();
        var legacy = Row(false);
        legacy.MarkProcessed();

        await Publish(legacy, sends, new FakeEncoder());

        Assert.Equal(0, sends.Json);
        Assert.Empty(sends.Avro);
        Assert.Null(legacy.AvroPublishedAt);
    }

    [Theory]
    [InlineData(false, AvroTopic)]
    [InlineData(true, null)]
    public async Task Without_a_registry_or_an_avro_topic_only_the_json_topic_is_used(bool configured, string? topic)
    {
        var sends = new Sends();
        var row = Row(true, [1]);

        await Publish(row, sends, new FakeEncoder(configured), topic);

        Assert.Equal(1, sends.Json);
        Assert.Empty(sends.Avro);
        Assert.NotNull(row.ProcessedAt);
        Assert.Null(row.AvroPublishedAt);
    }

    [Fact]
    public async Task A_row_that_is_done_on_both_sides_sends_nothing()
    {
        var sends = new Sends();
        var row = Row(true, [1]);
        await Publish(row, sends, new FakeEncoder());

        await Publish(row, sends, new FakeEncoder());

        Assert.Equal(1, sends.Json);
        Assert.Single(sends.Avro);
    }
}
