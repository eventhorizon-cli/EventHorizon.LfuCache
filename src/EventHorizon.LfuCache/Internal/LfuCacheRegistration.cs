namespace EventHorizon.LfuCache.Internal;

internal sealed record LfuCacheRegistration(
    string Keyspace,
    Type KeyType,
    Type ValueType,
    Type ServiceType);
