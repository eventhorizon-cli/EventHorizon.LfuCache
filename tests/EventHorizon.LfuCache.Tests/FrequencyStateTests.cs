using EventHorizon.LfuCache.Storage;

namespace EventHorizon.LfuCache.Tests;

public sealed class FrequencyStateTests
{
    [Fact]
    public void GetFrequency_ElapsedEpochs_AppliesAllLogicalHalves()
    {
        var entry = CacheEntry<int>.Completed(
            value: 1,
            frequency: 8,
            frequencyEpoch: 0,
            lastAccessTicks: 0,
            createdTicks: 0,
            expiresAtTicks: long.MaxValue);

        Assert.Equal(1L, entry.GetFrequency(3));
        Assert.Equal(1L, entry.GetFrequency(6));
    }

    [Fact]
    public void RecordAccess_AfterElapsedEpochs_NormalizesBeforeIncrementing()
    {
        var entry = CacheEntry<int>.Completed(
            value: 1,
            frequency: 8,
            frequencyEpoch: 0,
            lastAccessTicks: 0,
            createdTicks: 0,
            expiresAtTicks: long.MaxValue);

        entry.RecordAccess(3, 10);

        Assert.Equal(2L, entry.GetFrequency(3));
        Assert.Equal(1L, entry.GetFrequency(4));
    }

    [Fact]
    public void RecordAccess_FrequencyAtMaximum_Saturates()
    {
        var entry = CacheEntry<int>.Completed(
            value: 1,
            frequency: uint.MaxValue,
            frequencyEpoch: 0,
            lastAccessTicks: 0,
            createdTicks: 0,
            expiresAtTicks: long.MaxValue);

        entry.RecordAccess(0, 10);

        Assert.Equal((long)uint.MaxValue, entry.GetFrequency(0));
    }
}
