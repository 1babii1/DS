using Shared.Avro;
using Shared.Outbox;

namespace Shared.UnitTests;

// Events go out as Avro only. What matters is that a row is marked done only after a successful send, whatever fails
// before it, and that the bytes are the staged ones when there are any and are made from the stored JSON when not.
public class OutboxRowPublisherTests
{
    private sealed class FakeEncoder : IEventAvroEncoder
    {
        public bool IsConfigured => true;

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
        public List<byte[]> Sent { get; } = [];

        public bool Fail { get; set; }

        public Task Send(OutboxMessage m, byte[] bytes, CancellationToken ct)
        {
            if (Fail)
            {
                throw new InvalidOperationException("broker said no");
            }

            Sent.Add(bytes);
            return Task.CompletedTask;
        }
    }

    private static OutboxMessage Row(byte[]? bytes = null)
    {
        var row = OutboxMessage.Create("EmployeeHired", "agg", "{\"EmployeeId\":\"x\"}");
        if (bytes is not null)
        {
            row.AttachAvro(bytes);
        }

        return row;
    }

    private static Task Publish(OutboxMessage row, Sends sends, FakeEncoder encoder) =>
        OutboxRowPublisher.PublishAsync(row, sends.Send, encoder, CancellationToken.None);

    [Fact]
    public async Task A_row_with_its_avro_bytes_sends_those_and_is_marked_done()
    {
        var sends = new Sends();
        var encoder = new FakeEncoder();
        var row = Row([1, 2, 3]);

        await Publish(row, sends, encoder);

        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(sends.Sent));
        Assert.Equal(0, encoder.JsonEncodes);
        Assert.NotNull(row.ProcessedAt);
    }

    [Fact]
    public async Task A_row_written_while_the_registry_was_down_is_encoded_from_its_json_at_publish_time()
    {
        var sends = new Sends();
        var encoder = new FakeEncoder();
        var row = Row();

        await Publish(row, sends, encoder);

        Assert.Equal(1, encoder.JsonEncodes);
        Assert.Equal(new byte[] { 9, 9 }, Assert.Single(sends.Sent));
        Assert.NotNull(row.ProcessedAt);
    }

    [Fact]
    public async Task A_broker_failure_leaves_the_row_pending()
    {
        var sends = new Sends { Fail = true };
        var row = Row([1]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Publish(row, sends, new FakeEncoder()));

        Assert.Null(row.ProcessedAt);
        Assert.Empty(sends.Sent);

        sends.Fail = false;
        await Publish(row, sends, new FakeEncoder());

        Assert.NotNull(row.ProcessedAt);
        Assert.Single(sends.Sent);
    }

    [Fact]
    public async Task A_registry_that_is_down_sends_nothing_and_leaves_the_row_pending()
    {
        var sends = new Sends();
        var encoder = new FakeEncoder { Fail = true };
        var row = Row();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Publish(row, sends, encoder));

        Assert.Empty(sends.Sent);
        Assert.Null(row.ProcessedAt);
    }
}
