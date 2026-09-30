using System.Globalization;
using System.Text;
using Shared.Outbox;

namespace Shared.UnitTests;

// The moment an event happened has to travel with the message: a consumer's own clock only says when the message
// arrived, which is wrong for anything replayed, delayed or consumed after an outage.
public class OutboxMessageHeadersTests
{
    [Fact]
    public void The_published_message_carries_when_the_event_happened_in_a_round_trippable_utc_form()
    {
        var message = OutboxMessage.Create("DepartmentCreated", "dep-1", "{}");

        var kafka = OutboxMessageHeaders.ToKafkaMessage(message);

        Assert.True(kafka.Headers.TryGetLastBytes(OutboxMessageHeaders.OccurredAt, out var bytes));
        var parsed = DateTime.Parse(
            Encoding.UTF8.GetString(bytes), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal(message.OccurredAt, parsed);
    }

    [Fact]
    public void The_message_id_type_key_and_payload_are_what_they_always_were()
    {
        var message = OutboxMessage.Create("DepartmentCreated", "dep-1", """{"a":1}""");

        var kafka = OutboxMessageHeaders.ToKafkaMessage(message);

        Assert.Equal("dep-1", kafka.Key);
        Assert.Equal("""{"a":1}""", kafka.Value);
        Assert.True(kafka.Headers.TryGetLastBytes("message-id", out var id));
        Assert.Equal(message.Id.ToString(), Encoding.UTF8.GetString(id));
        Assert.True(kafka.Headers.TryGetLastBytes("message-type", out var type));
        Assert.Equal("DepartmentCreated", Encoding.UTF8.GetString(type));
    }

    [Theory]
    [InlineData("2026-03-05T10:15:30.1234567Z", "2026-03-05T10:15:30.1234567Z")]
    [InlineData("2026-03-05T10:15:30+02:00", "2026-03-05T08:15:30.0000000Z")]
    public void A_header_is_read_back_as_utc(string header, string expected)
    {
        var read = OutboxMessageHeaders.TryReadOccurredAt(Encoding.UTF8.GetBytes(header), out var value);

        Assert.True(read);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(DateTime.Parse(expected, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("0001-01-01T00:00:00Z")]
    public void A_header_that_is_not_a_usable_time_is_not_read(string header)
    {
        Assert.False(OutboxMessageHeaders.TryReadOccurredAt(Encoding.UTF8.GetBytes(header), out _));
    }
}
