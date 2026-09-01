using Microsoft.Extensions.Logging;

namespace EventHorizon.LfuCache.Maintenance;

internal static partial class LfuCacheMaintenanceLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "LFU maintenance failed for keyspace {Keyspace}; the loop will retry after a delay")]
    public static partial void StoreFailed(ILogger logger, string keyspace, Exception exception);
}
