namespace EventHorizon.LfuCache.Registration;

internal sealed record LfuCacheRegistration(
    string Keyspace,
    Type KeyType,
    Type ValueType,
    Type ServiceType);
