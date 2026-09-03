using EventHorizon.LfuCache.Metrics;
using OpenTelemetry.Metrics;

namespace EventHorizon.LfuCache;

/// <summary>Provides OpenTelemetry builder extensions for EventHorizon LFU cache metrics.</summary>
public static class LfuCacheOpenTelemetryExtensions
{
    /// <summary>Adds the EventHorizon LFU cache meter to a metrics provider builder.</summary>
    /// <param name="builder">The metrics provider builder to configure.</param>
    /// <returns>The supplied <paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static MeterProviderBuilder AddLfuCacheInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddMeter(LfuCacheMetrics.MeterName);
    }
}
