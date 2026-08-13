namespace EventHorizon.LfuCache.Internal;

internal static class LfuCacheOptionsValidator
{
    private static readonly TimeSpan _minimumInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _maximumMaintenanceInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan _maximumDecayInterval = TimeSpan.FromHours(24);

    public static bool TryValidate(LfuCacheOptions options, out string[] failures)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();

        if (options.Capacity < 1)
        {
            errors.Add("Capacity must be at least 1.");
        }

        if (options.EvictionRatio is <= 0 or > 0.5 || double.IsNaN(options.EvictionRatio))
        {
            errors.Add("EvictionRatio must be greater than 0 and no greater than 0.5.");
        }

        if (options.OverflowRatio is < 0 or > 0.5 || double.IsNaN(options.OverflowRatio))
        {
            errors.Add("OverflowRatio must be between 0 and 0.5.");
        }

        if (options.DefaultExpiry is { } defaultExpiry && defaultExpiry <= TimeSpan.Zero)
        {
            errors.Add("DefaultExpiry must be positive when specified.");
        }

        if (options.MaintenanceInterval < _minimumInterval
            || options.MaintenanceInterval > _maximumMaintenanceInterval)
        {
            errors.Add("MaintenanceInterval must be between 1 second and 1 hour.");
        }

        if (options.DecayInterval < _minimumInterval || options.DecayInterval > _maximumDecayInterval)
        {
            errors.Add("DecayInterval must be between 1 second and 24 hours.");
        }

        failures = [.. errors];
        return failures.Length == 0;
    }
}
