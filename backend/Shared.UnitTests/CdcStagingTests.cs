using Microsoft.Extensions.Configuration;
using Shared.Avro;
using Shared.Outbox;

namespace Shared.UnitTests;

// In CDC mode (ADR 0030) Debezium relays the bytes of a row's Avro column and cannot make them from the JSON. So staging has to
// succeed or the write has to fail, and the row has to be marked delivered because no publisher will mark it.
public class CdcStagingTests
{
    private sealed class Inner(bool encodes) : IEventAvroEncoder
    {
        public bool IsConfigured => true;

        public bool IsReady => encodes;

        public bool TryEncode(string eventType, object payload, out byte[] bytes)
        {
            bytes = encodes ? [0, 0, 0, 0, 7, 1] : [];
            return encodes;
        }

        public Task<byte[]> EncodeJsonAsync(string eventType, string json, CancellationToken cancellationToken) => Task.FromResult<byte[]>([]);
    }

    private static OutboxMessage Row() => OutboxMessage.Create("EmployeeHired", "agg", "{}");

    [Fact]
    public void In_polling_mode_a_staged_row_keeps_waiting_for_the_publisher()
    {
        var row = Row();

        row.StageAvro(new Inner(encodes: true), "EmployeeHired", new object());

        Assert.NotNull(row.AvroPayload);
        Assert.Null(row.ProcessedAt);
    }

    [Fact]
    public void In_polling_mode_a_row_that_could_not_be_staged_is_still_written_for_the_publisher_to_encode_later()
    {
        var row = Row();

        row.StageAvro(new Inner(encodes: false), "EmployeeHired", new object());

        Assert.Null(row.AvroPayload);
        Assert.Null(row.ProcessedAt);
    }

    [Fact]
    public void In_cdc_mode_a_staged_row_is_marked_delivered_at_once()
    {
        var row = Row();

        row.StageAvro(new CdcEventAvroEncoder(new Inner(encodes: true)), "EmployeeHired", new object());

        Assert.Equal(new byte[] { 0, 0, 0, 0, 7, 1 }, row.AvroPayload);
        Assert.NotNull(row.ProcessedAt);
    }

    [Fact]
    public void In_cdc_mode_an_event_that_cannot_be_encoded_refuses_the_write_instead_of_leaving_an_unpublishable_row()
    {
        var row = Row();

        var failure = Assert.Throws<EventNotEncodableException>(
            () => row.StageAvro(new CdcEventAvroEncoder(new Inner(encodes: false)), "EmployeeHired", new object()));

        Assert.Contains("EmployeeHired", failure.Message);
        Assert.Null(row.AvroPayload);
        Assert.Null(row.ProcessedAt);
    }

    [Fact]
    public void With_no_encoder_at_all_nothing_changes()
    {
        var row = Row();

        row.StageAvro(null, "EmployeeHired", new object());

        Assert.Null(row.AvroPayload);
        Assert.Null(row.ProcessedAt);
    }

    [Theory]
    [InlineData(null, OutboxMode.Polling)]
    [InlineData("", OutboxMode.Polling)]
    [InlineData("Polling", OutboxMode.Polling)]
    [InlineData("Cdc", OutboxMode.Cdc)]
    [InlineData("cdc", OutboxMode.Cdc)]
    [InlineData("nonsense", OutboxMode.Polling)]
    public void The_mode_comes_from_configuration_and_defaults_to_polling(string? value, OutboxMode expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(value is null ? [] : [new KeyValuePair<string, string?>(OutboxModeExtensions.ConfigurationKey, value)])
            .Build();

        Assert.Equal(expected, configuration.OutboxMode());
    }
}
