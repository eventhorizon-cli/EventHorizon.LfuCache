namespace EventHorizon.LfuCache;

/// <summary>Configures one LFU cache keyspace.</summary>
/// <remarks>
/// Options are validated when the cache is created. Invalid values received during configuration reload are rejected,
/// and the cache continues to use its previous valid configuration.
/// </remarks>
public sealed class LfuCacheOptions
{
    /// <summary>Gets or sets the maximum number of entries in this keyspace.</summary>
    /// <value>A value greater than or equal to 1. The default is 10,000.</value>
    public int Capacity { get; set; } = 10_000;

    /// <summary>Gets or sets the fraction of capacity removed in one eviction batch.</summary>
    /// <value>A value greater than 0 and less than or equal to 0.5. The default is 0.1.</value>
    public double EvictionRatio { get; set; } = 0.1;

    /// <summary>Gets or sets the expiration used when an entry-specific expiry is not supplied.</summary>
    /// <value>
    /// A positive duration, or <see langword="null"/> to disable expiration by default. The default is
    /// <see langword="null"/>. Unlike an entry-specific expiry, <see cref="TimeSpan.Zero"/> is not valid.
    /// </value>
    public TimeSpan? DefaultExpiry { get; set; }

    /// <summary>Gets or sets the interval between incremental expiration scans.</summary>
    /// <value>A duration from 1 second through 1 hour, inclusive. The default is 10 seconds.</value>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the access-frequency half-life.</summary>
    /// <value>A duration from 1 second through 24 hours, inclusive. The default is 1 minute.</value>
    public TimeSpan DecayInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets or sets the fraction by which the keyspace may temporarily exceed its capacity.</summary>
    /// <value>A value from 0 through 0.5, inclusive. The default is 0.05.</value>
    public double OverflowRatio { get; set; } = 0.05;
}
