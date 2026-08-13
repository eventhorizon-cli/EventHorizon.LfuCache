namespace EventHorizon.LfuCache.Internal;

internal sealed class LfuCacheCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LfuCacheRegistration> _registrations =
        new(StringComparer.Ordinal);

    public void Add(string keyspace, Type keyType, Type valueType, Type serviceType)
    {
        lock (_gate)
        {
            if (_registrations.TryGetValue(keyspace, out var existing))
            {
                if (existing.KeyType == keyType && existing.ValueType == valueType)
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"LFU cache keyspace '{keyspace}' is already registered for " +
                    $"<{existing.KeyType.Name}, {existing.ValueType.Name}> and cannot also register " +
                    $"<{keyType.Name}, {valueType.Name}>. Use a different keyspace.");
            }

            _registrations.Add(keyspace, new LfuCacheRegistration(keyspace, keyType, valueType, serviceType));
        }
    }

    public LfuCacheRegistration[] GetRegistrations()
    {
        lock (_gate)
        {
            return [.. _registrations.Values];
        }
    }

    public bool TryGetRegistration(string keyspace, out LfuCacheRegistration registration)
    {
        lock (_gate)
        {
            return _registrations.TryGetValue(keyspace, out registration!);
        }
    }
}
