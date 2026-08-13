using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EventHorizon.LfuCache.Internal;

internal sealed class LfuCache<TKey, TValue> : ILfuCache<TKey, TValue>, ILfuCacheHandle, IDisposable
    where TKey : notnull
{
    private static readonly TimeSpan _newEntryProtectionWindow = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _timeProvider;
    private readonly LfuCacheRegistry _registry;
    private readonly LfuCacheMetrics _metrics;
    private readonly ILogger _logger;
    private readonly IDisposable? _optionsSubscription;
    private readonly KeyValuePair<string, object?>[] _metricTags;
    private readonly object _scanGate = new();
    private readonly long _protectionWindowTicks;

    private LfuCacheStoreState<TKey, TValue> _state = new();
    private OptionsSnapshot _snapshot;
    private LfuCacheScanCursor<TKey, TValue>? _expirationCursor;
    private LfuCacheScanCursor<TKey, TValue>? _decayCursor;
    private long _nextMaintenanceTicks;
    private long _nextDecayTicks;
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _expirations;
    private long _evictionBatches;
    private int _evictionGate;
    private int _evictionRequested;
    private int _capacityContraction;
    private int _disposed;

    public LfuCache(
        string keyspace,
        IOptionsMonitor<LfuCacheOptions> optionsMonitor,
        TimeProvider timeProvider,
        LfuCacheRegistry registry,
        LfuCacheMetrics metrics,
        ILogger? logger = null)
    {
        Keyspace = keyspace;
        _timeProvider = timeProvider;
        _registry = registry;
        _metrics = metrics;
        _logger = logger ?? NullLogger.Instance;
        _metricTags =
        [
            new KeyValuePair<string, object?>("keyspace", keyspace),
            new KeyValuePair<string, object?>("value_type", typeof(TValue).FullName ?? typeof(TValue).Name),
        ];
        _protectionWindowTicks = TimestampMath.ToTimestampTicks(_newEntryProtectionWindow, timeProvider);

        var initialOptions = optionsMonitor.Get(keyspace);
        if (!LfuCacheOptionsValidator.TryValidate(initialOptions, out var failures))
        {
            throw new OptionsValidationException(keyspace, typeof(LfuCacheOptions), failures);
        }

        _snapshot = OptionsSnapshot.Create(initialOptions, timeProvider);
        var nowTicks = timeProvider.GetTimestamp();
        _nextMaintenanceTicks = TimestampMath.Add(nowTicks, _snapshot.MaintenanceIntervalTicks);
        _nextDecayTicks = TimestampMath.Add(nowTicks, _snapshot.DecayIntervalTicks);
        _optionsSubscription = optionsMonitor.OnChange(ApplyOptions);
    }

    public string Keyspace { get; }

    public Type KeyType => typeof(TKey);

    public Type ValueType => typeof(TValue);

    public int Count
    {
        get
        {
            var state = Volatile.Read(ref _state);
            var count = Volatile.Read(ref state.Count);
            return count <= 0 ? 0 : count >= int.MaxValue ? int.MaxValue : (int)count;
        }
    }

    public long NextDueTicks
    {
        get
        {
            if (Volatile.Read(ref _evictionRequested) != 0)
            {
                return long.MinValue;
            }

            return Math.Min(
                Volatile.Read(ref _nextMaintenanceTicks),
                Volatile.Read(ref _nextDecayTicks));
        }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);

        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (!state.Entries.TryGetValue(key, out var entry))
            {
                RecordMiss();
                value = default!;
                return false;
            }

            if (!ReferenceEquals(state, Volatile.Read(ref _state)))
            {
                continue;
            }

            if (!entry.IsCompleted)
            {
                RecordMiss();
                value = default!;
                return false;
            }

            var nowTicks = _timeProvider.GetTimestamp();
            if (nowTicks >= Volatile.Read(ref entry.ExpiresAtTicks))
            {
                RemoveExpired(state, key, entry);
                RecordMiss();
                value = default!;
                return false;
            }

            Interlocked.Increment(ref entry.Frequency);
            Volatile.Write(ref entry.LastAccessTicks, nowTicks);
            RecordHit();
            value = entry.Value;
            return true;
        }
    }

    public void Set(TKey key, TValue value, TimeSpan? expiry = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateExpiry(expiry);

        while (true)
        {
            var state = Volatile.Read(ref _state);
            var snapshot = Volatile.Read(ref _snapshot);
            var nowTicks = _timeProvider.GetTimestamp();
            var expiresAtTicks = GetExpiresAtTicks(expiry, snapshot, nowTicks);

            if (state.Entries.TryGetValue(key, out var observed))
            {
                var isLive = observed.IsCompleted && nowTicks < Volatile.Read(ref observed.ExpiresAtTicks);
                var frequency = isLive ? Math.Max(1, Volatile.Read(ref observed.Frequency)) : 1;
                var createdTicks = isLive ? observed.CreatedTicks : nowTicks;
                var replacement = CacheEntry<TValue>.Completed(
                    value,
                    frequency,
                    nowTicks,
                    createdTicks,
                    expiresAtTicks);

                if (!state.Entries.TryUpdate(key, replacement, observed))
                {
                    continue;
                }
            }
            else
            {
                var added = CacheEntry<TValue>.Completed(value, 1, nowTicks, nowTicks, expiresAtTicks);
                if (!state.Entries.TryAdd(key, added))
                {
                    continue;
                }

                Interlocked.Increment(ref state.Count);
            }

            if (!ReferenceEquals(state, Volatile.Read(ref _state)))
            {
                continue;
            }

            CheckWatermarks(state, snapshot);
            return;
        }
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory, TimeSpan? expiry = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return GetOrAddCoreAsync(
                key,
                (item, _) => new ValueTask<TValue>(factory(item)),
                expiry,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    public ValueTask<TValue> GetOrAddAsync(
        TKey key,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new ValueTask<TValue>(GetOrAddCoreAsync(key, factory, expiry, ct));
    }

    public bool Remove(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var state = Volatile.Read(ref _state);
        if (!state.Entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        return RemoveObserved(state, key, entry);
    }

    public void Clear()
    {
        lock (_scanGate)
        {
            Interlocked.Exchange(ref _state, new LfuCacheStoreState<TKey, TValue>());
            DisposeCursor(ref _expirationCursor);
            DisposeCursor(ref _decayCursor);
            Volatile.Write(ref _evictionRequested, 0);
            Volatile.Write(ref _capacityContraction, 0);
        }
    }

    public LfuCacheStats GetStats()
    {
        return new LfuCacheStats(
            Volatile.Read(ref _hits),
            Volatile.Read(ref _misses),
            Volatile.Read(ref _evictions),
            Volatile.Read(ref _expirations),
            Volatile.Read(ref _evictionBatches),
            Count,
            Volatile.Read(ref _snapshot).Capacity);
    }

    public void RunMaintenance(long nowTicks)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var expired = 0;
        var decayed = 0;

        var maintenanceDueTicks = Volatile.Read(ref _nextMaintenanceTicks);
        if (nowTicks >= maintenanceDueTicks)
        {
            expired = ScanExpired(snapshot.ScanBudget, nowTicks);
            var activeSnapshot = Volatile.Read(ref _snapshot);
            Interlocked.CompareExchange(
                ref _nextMaintenanceTicks,
                TimestampMath.Add(nowTicks, activeSnapshot.MaintenanceIntervalTicks),
                maintenanceDueTicks);
        }

        if (Volatile.Read(ref _evictionRequested) != 0)
        {
            EvictOneBatch(snapshot, nowTicks, false);
        }

        var decayDueTicks = Volatile.Read(ref _nextDecayTicks);
        if (nowTicks >= decayDueTicks)
        {
            decayed = ScanForDecay(snapshot.ScanBudget);
            var activeSnapshot = Volatile.Read(ref _snapshot);
            Interlocked.CompareExchange(
                ref _nextDecayTicks,
                TimestampMath.Add(nowTicks, activeSnapshot.DecayIntervalTicks),
                decayDueTicks);
        }

        if (expired != 0 || decayed != 0)
        {
            _logger.LogDebug(
                "LFU maintenance processed keyspace {Keyspace}: expired {ExpiredCount}, decayed {DecayedCount}",
                Keyspace,
                expired,
                decayed);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _optionsSubscription?.Dispose();

        lock (_scanGate)
        {
            DisposeCursor(ref _expirationCursor);
            DisposeCursor(ref _decayCursor);
        }
    }

    private async Task<TValue> GetOrAddCoreAsync(
        TKey key,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateExpiry(expiry);

        while (true)
        {
            var state = Volatile.Read(ref _state);

            if (state.Entries.TryGetValue(key, out var observed))
            {
                if (!ReferenceEquals(state, Volatile.Read(ref _state)))
                {
                    continue;
                }

                if (observed.IsCompleted)
                {
                    var nowTicks = _timeProvider.GetTimestamp();
                    if (nowTicks >= Volatile.Read(ref observed.ExpiresAtTicks))
                    {
                        RemoveExpired(state, key, observed);
                        continue;
                    }

                    Interlocked.Increment(ref observed.Frequency);
                    Volatile.Write(ref observed.LastAccessTicks, nowTicks);
                    RecordHit();
                    return observed.Value;
                }

                RecordMiss();
                return await observed.Inflight!.Task.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            CacheEntry<TValue>? pendingEntry = null;
            var operation = new InflightOperation<TValue>(
                () => RunFactoryAndPublishAsync(
                    state,
                    key,
                    pendingEntry!,
                    factory,
                    expiry,
                    cancellationToken));
            pendingEntry = CacheEntry<TValue>.Pending(operation, _timeProvider.GetTimestamp());

            if (!state.Entries.TryAdd(key, pendingEntry))
            {
                continue;
            }

            Interlocked.Increment(ref state.Count);

            if (!ReferenceEquals(state, Volatile.Read(ref _state)))
            {
                RemoveObserved(state, key, pendingEntry);
                continue;
            }

            RecordMiss();
            CheckWatermarks(state, Volatile.Read(ref _snapshot));
            return await operation.Task.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TValue> RunFactoryAndPublishAsync(
        LfuCacheStoreState<TKey, TValue> state,
        TKey key,
        CacheEntry<TValue> pendingEntry,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry,
        CancellationToken cancellationToken)
    {
        try
        {
            var value = await factory(key, cancellationToken).ConfigureAwait(false);
            var snapshot = Volatile.Read(ref _snapshot);
            var nowTicks = _timeProvider.GetTimestamp();
            var completed = CacheEntry<TValue>.Completed(
                value,
                Math.Max(1, Volatile.Read(ref pendingEntry.Frequency)),
                nowTicks,
                pendingEntry.CreatedTicks,
                GetExpiresAtTicks(expiry, snapshot, nowTicks));

            if (state.Entries.TryUpdate(key, completed, pendingEntry)
                && ReferenceEquals(state, Volatile.Read(ref _state)))
            {
                CheckWatermarks(state, snapshot);
            }

            return value;
        }
        catch
        {
            RemoveObserved(state, key, pendingEntry);
            throw;
        }
    }

    private void ApplyOptions(LfuCacheOptions options, string? name)
    {
        if (!StringComparer.Ordinal.Equals(KeyspaceNames.Normalize(name), Keyspace))
        {
            return;
        }

        try
        {
            var current = Volatile.Read(ref _snapshot);
            if (current.HasSameValues(options))
            {
                return;
            }

            if (!LfuCacheOptionsValidator.TryValidate(options, out var failures))
            {
                _logger.LogWarning(
                    "Rejected invalid LFU configuration for keyspace {Keyspace}: {Failures}",
                    Keyspace,
                    string.Join(" ", failures));
                return;
            }

            var replacement = OptionsSnapshot.Create(options, _timeProvider);
            Volatile.Write(ref _snapshot, replacement);
            var nowTicks = _timeProvider.GetTimestamp();

            if (current.MaintenanceInterval != replacement.MaintenanceInterval)
            {
                Volatile.Write(
                    ref _nextMaintenanceTicks,
                    TimestampMath.Add(nowTicks, replacement.MaintenanceIntervalTicks));
            }

            if (current.DecayInterval != replacement.DecayInterval)
            {
                Volatile.Write(
                    ref _nextDecayTicks,
                    TimestampMath.Add(nowTicks, replacement.DecayIntervalTicks));
            }

            if (Count > replacement.Capacity)
            {
                if (replacement.Capacity < current.Capacity)
                {
                    Volatile.Write(ref _capacityContraction, 1);
                }

                RequestEviction();
            }
            else
            {
                Volatile.Write(ref _capacityContraction, 0);
                _registry.Signal();
            }

            _logger.LogInformation(
                "Replaced LFU configuration for keyspace {Keyspace}: capacity {OldCapacity}->{NewCapacity}, " +
                "eviction ratio {OldEvictionRatio}->{NewEvictionRatio}, overflow ratio " +
                "{OldOverflowRatio}->{NewOverflowRatio}",
                Keyspace,
                current.Capacity,
                replacement.Capacity,
                current.EvictionRatio,
                replacement.EvictionRatio,
                current.OverflowRatio,
                replacement.OverflowRatio);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to apply LFU configuration for keyspace {Keyspace}; the previous snapshot remains active",
                Keyspace);
        }
    }

    private void CheckWatermarks(LfuCacheStoreState<TKey, TValue> state, OptionsSnapshot snapshot)
    {
        var count = Volatile.Read(ref state.Count);
        if (count > snapshot.Capacity)
        {
            RequestEviction();
        }

        if (count > snapshot.HardLimit)
        {
            EvictOneBatch(snapshot, _timeProvider.GetTimestamp(), true);
        }
    }

    private void RequestEviction()
    {
        Volatile.Write(ref _evictionRequested, 1);
        _registry.Signal();
    }

    private void EvictOneBatch(OptionsSnapshot snapshot, long nowTicks, bool synchronous)
    {
        if (Interlocked.CompareExchange(ref _evictionGate, 1, 0) != 0)
        {
            return;
        }

        try
        {
            snapshot = Volatile.Read(ref _snapshot);
            var state = Volatile.Read(ref _state);
            if (Volatile.Read(ref state.Count) <= snapshot.Capacity)
            {
                CompleteEvictionCheck();
                return;
            }

            if (synchronous)
            {
                _metrics.SynchronousEviction(_metricTags);
                _logger.LogWarning(
                    "Synchronous LFU eviction triggered for keyspace {Keyspace} at count {Count}",
                    Keyspace,
                    Volatile.Read(ref state.Count));
            }

            var stopwatch = Stopwatch.StartNew();
            var countBefore = Volatile.Read(ref state.Count);
            var batchSize = GetEvictionBatchSize(snapshot);
            var targetForBatch = Volatile.Read(ref _capacityContraction) != 0
                ? Math.Max(snapshot.Capacity, countBefore - batchSize)
                : snapshot.TargetLimit;
            var expired = RemoveExpiredDuringEviction(state, nowTicks, targetForBatch);
            var required = Math.Max(0L, Volatile.Read(ref state.Count) - targetForBatch);
            long minFrequency = 0;
            long maxFrequency = 0;
            var evicted = required == 0
                ? 0
                : SelectAndRemoveCandidates(
                    state,
                    SaturatingInt(required),
                    nowTicks,
                    out minFrequency,
                    out maxFrequency);
            stopwatch.Stop();

            Interlocked.Increment(ref _evictionBatches);
            _metrics.EvictionBatch(stopwatch.Elapsed.TotalMilliseconds, _metricTags);

            if (evicted != 0)
            {
                Interlocked.Add(ref _evictions, evicted);
                _metrics.Evicted(evicted, _metricTags);
            }

            _logger.LogInformation(
                "LFU eviction completed for keyspace {Keyspace}: target {Target}, evicted {Evicted}, " +
                "expired {Expired}, duration {DurationMs} ms, frequency range {MinFrequency}-{MaxFrequency}",
                Keyspace,
                targetForBatch,
                evicted,
                expired,
                stopwatch.Elapsed.TotalMilliseconds,
                minFrequency,
                maxFrequency);

            CompleteEvictionCheck();
        }
        finally
        {
            Volatile.Write(ref _evictionGate, 0);
        }
    }

    private int RemoveExpiredDuringEviction(
        LfuCacheStoreState<TKey, TValue> state,
        long nowTicks,
        long targetForBatch)
    {
        var removed = 0;

        foreach (var pair in state.Entries)
        {
            if (Volatile.Read(ref state.Count) <= targetForBatch)
            {
                break;
            }

            if (pair.Value.IsCompleted
                && nowTicks >= Volatile.Read(ref pair.Value.ExpiresAtTicks)
                && RemoveExpired(state, pair.Key, pair.Value))
            {
                removed++;
            }
        }

        return removed;
    }

    private int SelectAndRemoveCandidates(
        LfuCacheStoreState<TKey, TValue> state,
        int required,
        long nowTicks,
        out long minFrequency,
        out long maxFrequency)
    {
        var queue = new PriorityQueue<EvictionCandidate<TKey, TValue>, EvictionPriority>(
            required,
            WorstEvictionPriorityComparer.Instance);
        AddCandidates(state, queue, required, nowTicks, includeProtected: false);

        if (queue.Count < required)
        {
            AddCandidates(state, queue, required, nowTicks, includeProtected: true, protectedOnly: true);
        }

        var removed = 0;
        minFrequency = long.MaxValue;
        maxFrequency = 0;

        while (queue.TryDequeue(out var candidate, out _))
        {
            if (!RemoveObserved(state, candidate.Key, candidate.Entry))
            {
                continue;
            }

            var frequency = Volatile.Read(ref candidate.Entry.Frequency);
            minFrequency = Math.Min(minFrequency, frequency);
            maxFrequency = Math.Max(maxFrequency, frequency);
            removed++;
        }

        if (removed == 0)
        {
            minFrequency = 0;
        }

        return removed;
    }

    private void AddCandidates(
        LfuCacheStoreState<TKey, TValue> state,
        PriorityQueue<EvictionCandidate<TKey, TValue>, EvictionPriority> queue,
        int required,
        long nowTicks,
        bool includeProtected,
        bool protectedOnly = false)
    {
        foreach (var pair in state.Entries)
        {
            var entry = pair.Value;
            var isProtected = nowTicks - entry.CreatedTicks < _protectionWindowTicks;
            if ((!includeProtected && isProtected) || (protectedOnly && !isProtected))
            {
                continue;
            }

            var priority = new EvictionPriority(
                Volatile.Read(ref entry.Frequency),
                Volatile.Read(ref entry.LastAccessTicks));
            var candidate = new EvictionCandidate<TKey, TValue>(pair.Key, entry);

            if (queue.Count < required)
            {
                queue.Enqueue(candidate, priority);
                continue;
            }

            queue.TryPeek(out _, out var worstPriority);
            if (priority.CompareTo(worstPriority) < 0)
            {
                queue.Dequeue();
                queue.Enqueue(candidate, priority);
            }
        }
    }

    private int ScanExpired(int budget, long nowTicks)
    {
        return Scan(
            ref _expirationCursor,
            budget,
            (state, pair) => pair.Value.IsCompleted
                && nowTicks >= Volatile.Read(ref pair.Value.ExpiresAtTicks)
                && RemoveExpired(state, pair.Key, pair.Value));
    }

    private int ScanForDecay(int budget)
    {
        return Scan(
            ref _decayCursor,
            budget,
            (_, pair) =>
            {
                if (!pair.Value.IsCompleted)
                {
                    return false;
                }

                while (true)
                {
                    var frequency = Volatile.Read(ref pair.Value.Frequency);
                    var decayed = Math.Max(1, frequency >> 1);
                    if (Interlocked.CompareExchange(ref pair.Value.Frequency, decayed, frequency) == frequency)
                    {
                        return true;
                    }
                }
            });
    }

    private int Scan(
        ref LfuCacheScanCursor<TKey, TValue>? cursor,
        int budget,
        Func<LfuCacheStoreState<TKey, TValue>, KeyValuePair<TKey, CacheEntry<TValue>>, bool> action)
    {
        lock (_scanGate)
        {
            var state = Volatile.Read(ref _state);
            if (cursor is null || !ReferenceEquals(cursor.State, state))
            {
                DisposeCursor(ref cursor);
                cursor = new LfuCacheScanCursor<TKey, TValue>(state, state.Entries.GetEnumerator());
            }

            var affected = 0;
            for (var scanned = 0; scanned < budget; scanned++)
            {
                if (!cursor.Enumerator.MoveNext())
                {
                    DisposeCursor(ref cursor);
                    break;
                }

                if (action(cursor.State, cursor.Enumerator.Current))
                {
                    affected++;
                }
            }

            return affected;
        }
    }

    private bool RemoveExpired(
        LfuCacheStoreState<TKey, TValue> state,
        TKey key,
        CacheEntry<TValue> entry)
    {
        if (!RemoveObserved(state, key, entry))
        {
            return false;
        }

        Interlocked.Increment(ref _expirations);
        _metrics.Expired(_metricTags);
        return true;
    }

    private static bool RemoveObserved(
        LfuCacheStoreState<TKey, TValue> state,
        TKey key,
        CacheEntry<TValue> entry)
    {
        var collection = (ICollection<KeyValuePair<TKey, CacheEntry<TValue>>>)state.Entries;
        if (!collection.Remove(new KeyValuePair<TKey, CacheEntry<TValue>>(key, entry)))
        {
            return false;
        }

        Interlocked.Decrement(ref state.Count);
        return true;
    }

    private long GetExpiresAtTicks(TimeSpan? expiry, OptionsSnapshot snapshot, long nowTicks)
    {
        if (expiry == TimeSpan.Zero)
        {
            return long.MaxValue;
        }

        var effectiveExpiry = expiry ?? snapshot.DefaultExpiry;
        return effectiveExpiry is null
            ? long.MaxValue
            : TimestampMath.Add(nowTicks, TimestampMath.ToTimestampTicks(effectiveExpiry.Value, _timeProvider));
    }

    private static void ValidateExpiry(TimeSpan? expiry)
    {
        if (expiry is { } value && value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(expiry), expiry, "Expiry cannot be negative.");
        }
    }

    private void RecordHit()
    {
        Interlocked.Increment(ref _hits);
        _metrics.Hit(_metricTags);
    }

    private void RecordMiss()
    {
        Interlocked.Increment(ref _misses);
        _metrics.Miss(_metricTags);
    }

    private static int SaturatingInt(long value)
    {
        return value >= int.MaxValue ? int.MaxValue : (int)value;
    }

    private static int GetEvictionBatchSize(OptionsSnapshot snapshot)
    {
        return Math.Max(1, snapshot.Capacity - snapshot.TargetLimit);
    }

    private void CompleteEvictionCheck()
    {
        Interlocked.Exchange(ref _evictionRequested, 0);

        var activeSnapshot = Volatile.Read(ref _snapshot);
        var state = Volatile.Read(ref _state);
        if (Volatile.Read(ref state.Count) > activeSnapshot.Capacity)
        {
            RequestEviction();
        }
        else
        {
            Volatile.Write(ref _capacityContraction, 0);
        }
    }

    private static void DisposeCursor(ref LfuCacheScanCursor<TKey, TValue>? cursor)
    {
        cursor?.Dispose();
        cursor = null;
    }
}
