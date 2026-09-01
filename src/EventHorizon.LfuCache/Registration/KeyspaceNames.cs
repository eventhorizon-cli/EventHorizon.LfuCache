namespace EventHorizon.LfuCache.Registration;

internal static class KeyspaceNames
{
    public const string Default = "default";

    public static string Normalize(string? keyspace)
    {
        return string.IsNullOrWhiteSpace(keyspace)
            ? Default
            : keyspace.Trim().ToLowerInvariant();
    }
}
