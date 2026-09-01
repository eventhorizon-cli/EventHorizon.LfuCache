using Microsoft.Extensions.Logging;

namespace EventHorizon.LfuCache.Storage;

internal static partial class LfuCacheLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "LFU maintenance processed keyspace {Keyspace}: expired {ExpiredCount}, decayed {DecayedCount}")]
    public static partial void MaintenanceProcessed(
        ILogger logger,
        string keyspace,
        int expiredCount,
        int decayedCount);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Rejected invalid LFU configuration for keyspace {Keyspace}: {Failures}")]
    public static partial void InvalidOptionsRejected(ILogger logger, string keyspace, string failures);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Replaced LFU configuration for keyspace {Keyspace}: capacity {OldCapacity}->{NewCapacity}, " +
            "eviction ratio {OldEvictionRatio}->{NewEvictionRatio}, overflow ratio " +
            "{OldOverflowRatio}->{NewOverflowRatio}")]
    public static partial void OptionsReplaced(
        ILogger logger,
        string keyspace,
        int oldCapacity,
        int newCapacity,
        double oldEvictionRatio,
        double newEvictionRatio,
        double oldOverflowRatio,
        double newOverflowRatio);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "Failed to apply LFU configuration for keyspace {Keyspace}; the previous snapshot remains active")]
    public static partial void OptionsApplyFailed(ILogger logger, string keyspace, Exception exception);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Synchronous LFU eviction triggered for keyspace {Keyspace} at count {Count}")]
    public static partial void SynchronousEvictionTriggered(ILogger logger, string keyspace, long count);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Information,
        Message = "LFU eviction completed for keyspace {Keyspace}: target {Target}, evicted {Evicted}, " +
            "expired {Expired}, duration {DurationMs} ms, frequency range {MinFrequency}-{MaxFrequency}")]
    public static partial void EvictionCompleted(
        ILogger logger,
        string keyspace,
        long target,
        int evicted,
        int expired,
        double durationMs,
        long minFrequency,
        long maxFrequency);
}
