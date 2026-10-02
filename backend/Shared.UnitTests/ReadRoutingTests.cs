using Shared.Consistency;

namespace Shared.UnitTests;

// Read-your-writes on a lagging replica (ADR 0025). The decision is a function of the caller's token, how far the replica has
// replayed, and the clock, so it runs here without a database; the real replica is exercised by the drill in the ADR.
public class ReadRoutingTests
{
    private sealed class ScriptedReplica(params ulong?[] positions) : IReplayPosition
    {
        private int _next;

        public int Asked => _next;

        public Task<ulong?> ReplayedAsync(CancellationToken cancellationToken)
        {
            var value = positions[Math.Min(_next, positions.Length - 1)];
            _next++;
            return Task.FromResult(value);
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly ReadRoutingOptions Options = new() { MaxWait = TimeSpan.FromMilliseconds(500), PollInterval = TimeSpan.FromMilliseconds(100) };

    private static Task<ReadTarget> Choose(ulong? token, ScriptedReplica replica, FakeClock? clock = null)
    {
        clock ??= new FakeClock();
        return ReadRouter.ChooseAsync(token, replica, Options, (d, _) =>
        {
            clock.Now += d;
            return Task.CompletedTask;
        }, clock, CancellationToken.None);
    }

    [Fact]
    public async Task A_read_with_no_token_goes_to_the_replica_without_asking_how_far_it_is()
    {
        var replica = new ScriptedReplica(1);

        Assert.Equal(ReadTarget.Replica, await Choose(null, replica));
        Assert.Equal(0, replica.Asked);
    }

    [Fact]
    public async Task A_replica_that_has_replayed_the_callers_write_serves_the_read()
    {
        Assert.Equal(ReadTarget.Replica, await Choose(100, new ScriptedReplica(100)));
        Assert.Equal(ReadTarget.Replica, await Choose(100, new ScriptedReplica(250)));
    }

    [Fact]
    public async Task A_replica_one_position_behind_does_not_serve_the_read_once_the_wait_is_over()
    {
        Assert.Equal(ReadTarget.Primary, await Choose(100, new ScriptedReplica(99)));
    }

    [Fact]
    public async Task A_replica_that_catches_up_within_the_wait_serves_the_read()
    {
        var replica = new ScriptedReplica(50, 80, 100);

        Assert.Equal(ReadTarget.Replica, await Choose(100, replica));
        Assert.Equal(3, replica.Asked);
    }

    [Fact]
    public async Task The_wait_is_bounded()
    {
        var clock = new FakeClock();
        var start = clock.Now;
        var replica = new ScriptedReplica(1);

        Assert.Equal(ReadTarget.Primary, await Choose(100, replica, clock));

        Assert.True(clock.Now - start >= Options.MaxWait);
        Assert.True(clock.Now - start <= Options.MaxWait + Options.PollInterval);
        Assert.True(replica.Asked <= 7);
    }

    [Fact]
    public async Task A_replica_that_cannot_be_asked_is_not_waited_for()
    {
        var replica = new ScriptedReplica((ulong?)null);

        Assert.Equal(ReadTarget.Primary, await Choose(100, replica));
        Assert.Equal(1, replica.Asked);
    }

    [Theory]
    [InlineData("0/0", 0UL)]
    [InlineData("0/1", 1UL)]
    [InlineData("16/B374D848", 0x16B374D848UL)]
    [InlineData("FFFFFFFF/FFFFFFFF", ulong.MaxValue)]
    public void A_position_round_trips(string text, ulong value)
    {
        Assert.True(Lsn.TryParse(text, out var parsed));
        Assert.Equal(value, parsed);
        Assert.Equal(text, Lsn.Format(parsed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("/")]
    [InlineData("1/")]
    [InlineData("/1")]
    [InlineData("1/2/3")]
    [InlineData("G/1")]
    [InlineData("100000000/1")]
    [InlineData("1/100000000")]
    [InlineData("-1/1")]
    [InlineData("1; DROP TABLE x/1")]
    [InlineData("0000000000000000/1")]
    public void Anything_that_is_not_a_position_is_not_parsed(string? text)
    {
        Assert.False(Lsn.TryParse(text, out _));
    }

    [Fact]
    public void Positions_compare_as_numbers_not_as_text()
    {
        Assert.True(Lsn.TryParse("9/0", out var nine));
        Assert.True(Lsn.TryParse("10/0", out var sixteen));

        Assert.True(sixteen > nine);
    }
}
