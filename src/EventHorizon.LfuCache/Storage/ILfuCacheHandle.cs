namespace EventHorizon.LfuCache.Storage;

internal interface ILfuCacheHandle
{
    string Keyspace { get; }

    Type KeyType { get; }

    Type ValueType { get; }

    long NextDueTicks { get; }

    void Clear();

    LfuCacheStats GetStats();

    void RunMaintenance(long nowTicks);
}
