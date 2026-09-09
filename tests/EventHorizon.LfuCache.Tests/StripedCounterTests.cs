using EventHorizon.LfuCache.Metrics;

namespace EventHorizon.LfuCache.Tests;

public sealed class StripedCounterTests
{
    [Fact]
    public void Read_NewCounter_ReturnsZero()
    {
        var counter = new StripedCounter(4);

        Assert.Equal(0, counter.Read());
    }

    [Fact]
    public async Task Read_ConcurrentIncrements_ReturnsExactTotal()
    {
        var counter = new StripedCounter(8);
        const int workerCount = 8;
        const int incrementsPerWorker = 10_000;

        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() =>
            {
                for (var increment = 0; increment < incrementsPerWorker; increment++)
                {
                    counter.Increment();
                }
            }))
            .ToArray();

        await Task.WhenAll(workers);

        Assert.Equal((long)workerCount * incrementsPerWorker, counter.Read());
    }
}
