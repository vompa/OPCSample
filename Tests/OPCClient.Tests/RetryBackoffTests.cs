using OPCClient.Opc;

namespace OPCClient.Tests;

public class RetryBackoffTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]   // 32 s wären es, gedeckelt auf 30 s
    [InlineData(50, 30)]
    public void Delay_doubles_and_is_capped(int attempt, double expectedSeconds) =>
        Assert.Equal(expectedSeconds, RetryBackoff.Delay(attempt).TotalSeconds, precision: 6);

    [Fact]
    public void Attempts_below_one_are_treated_as_first_attempt() =>
        Assert.Equal(RetryBackoff.Delay(1), RetryBackoff.Delay(-3));

    [Fact]
    public void Jitter_moves_the_delay_by_at_most_twenty_percent()
    {
        Assert.Equal(0.8, RetryBackoff.Delay(1, jitter: -1).TotalSeconds, precision: 6);
        Assert.Equal(1.2, RetryBackoff.Delay(1, jitter: 1).TotalSeconds, precision: 6);
    }

    [Fact]
    public void Jitter_never_pushes_the_delay_beyond_the_maximum() =>
        Assert.Equal(RetryBackoff.Max, RetryBackoff.Delay(10, jitter: 1));

    [Fact]
    public void Next_stays_within_the_jitter_range()
    {
        for (var i = 0; i < 200; i++)
        {
            var delay = RetryBackoff.Next(3).TotalSeconds;
            Assert.InRange(delay, 4 * 0.8, 4 * 1.2);
        }
    }
}
