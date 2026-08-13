namespace EventHorizon.LfuCache;

/// <summary>Configures one LFU cache keyspace.</summary>
public sealed class LfuCacheOptions
{
    /// <summary>Gets or sets the maximum number of entries in this keyspace.</summary>
    public int Capacity { get; set; } = 10_000;

    /// <summary>Gets or sets the fraction of capacity removed in one eviction batch.</summary>
    public double EvictionRatio { get; set; } = 0.1;

    /// <summary>Gets or sets the expiration used when an entry-specific expiry is not supplied.</summary>
    public TimeSpan? DefaultExpiry { get; set; }

    /// <summary>Gets or sets the interval between incremental expiration scans.</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the access-frequency half-life.</summary>
    public TimeSpan DecayInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets or sets the fraction by which the keyspace may temporarily exceed its capacity.</summary>
    public double OverflowRatio { get; set; } = 0.05;
}
