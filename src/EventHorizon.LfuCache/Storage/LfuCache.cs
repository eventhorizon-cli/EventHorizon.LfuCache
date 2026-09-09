using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using EventHorizon.LfuCache.Configuration;
using EventHorizon.LfuCache.Eviction;
using EventHorizon.LfuCache.Maintenance;
using EventHorizon.LfuCache.Metrics;
using EventHorizon.LfuCache.Registration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EventHorizon.LfuCache.Storage;

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
    private readonly PriorityQueue<EvictionCandidate<TKey, TValue>, EvictionPriority> _evictionCandidates =
        new(WorstEvictionPriorityComparer.Instance);
    private readonly StripedCounter _hits = new();
    private readonly StripedCounter _misses = new();

    private LfuCacheStoreState<TKey, TValue> _state = new();
    private OptionsSnapshot _snapshot;
    private LfuCacheScanCursor<TKey, TValue>? _expirationCursor;
    private LfuCacheScanCursor<TKey, TValue>? _decayCursor;
    private long _nextMaintenanceTicks;
    private long _nextDecayTicks;
    private long _nextEvictionTicks = long.MaxValue;
    private long _evictions;
    private long _expirations;
    private long _evictionBatches;
    private int _evictionGate;
    private int _capacityContraction;
    private int _inflightCount;
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
            var nextDueTicks = Math.Min(
                Volatile.Read(ref _nextMaintenanceTicks),
                Volatile.Read(ref _nextDecayTicks));
            return Math.Min(nextDueTicks, Volatile.Read(ref _nextEvictionTicks));
        }
    }

    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (TryGetLiveValue(key, out value))
        {
            return true;
        }

        RecordMiss();
        return false;
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
            var frequencyEpoch = snapshot.GetFrequencyEpoch(nowTicks);

            if (state.Entries.TryGetValue(key, out var observed))
            {
                var isLive = observed.IsCompleted && nowTicks < Volatile.Read(ref observed.ExpiresAtTicks);
                var frequency = isLive ? observed.GetFrequency(frequencyEpoch) : 1;
                var createdTicks = isLive ? observed.CreatedTicks : nowTicks;
                var replacement = CacheEntry<TValue>.Completed(
                    value,
                    frequency,
                    frequencyEpoch,
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
                var added = CacheEntry<TValue>.Completed(value, 1, frequencyEpoch, nowTicks, nowTicks, expiresAtTicks);
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
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        ValidateExpiry(expiry);

        if (TryGetLiveValue(key, out var value))
        {
            return value;
        }

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        ValidateExpiry(expiry);

        if (TryGetLiveValue(key, out var value))
        {
            return new ValueTask<TValue>(value);
        }

        return new ValueTask<TValue>(GetOrAddCoreAsync(key, factory, expiry, cancellationToken));
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
            Volatile.Write(ref _nextEvictionTicks, long.MaxValue);
            Volatile.Write(ref _capacityContraction, 0);
            Interlocked.Exchange(ref _state, new LfuCacheStoreState<TKey, TValue>());
            DisposeCursor(ref _expirationCursor);
            DisposeCursor(ref _decayCursor);
        }
    }

    public LfuCacheStats GetStats()
    {
        return new LfuCacheStats(
            _hits.Read(),
            _misses.Read(),
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

        if (nowTicks >= Volatile.Read(ref _nextEvictionTicks))
        {
            EvictOneBatch(snapshot, nowTicks, false);
        }

        var decayDueTicks = Volatile.Read(ref _nextDecayTicks);
        if (nowTicks >= decayDueTicks)
        {
            decayed = ScanForDecay(snapshot.ScanBudget, snapshot.GetFrequencyEpoch(nowTicks));
            var activeSnapshot = Volatile.Read(ref _snapshot);
            Interlocked.CompareExchange(
                ref _nextDecayTicks,
                TimestampMath.Add(nowTicks, activeSnapshot.DecayIntervalTicks),
                decayDueTicks);
        }

        if (expired != 0 || decayed != 0)
        {
            LfuCacheLog.MaintenanceProcessed(_logger, Keyspace, expired, decayed);
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

                    observed.RecordAccess(Volatile.Read(ref _snapshot).GetFrequencyEpoch(nowTicks), nowTicks);
                    RecordHit();
                    return observed.Value;
                }

                var waitTask = observed.Inflight!.TryWaitAsync(cancellationToken);
                if (waitTask is null)
                {
                    RemoveObserved(state, key, observed);
                    continue;
                }

                RecordMiss();
                return await waitTask.ConfigureAwait(false);
            }

            var slotLimit = Volatile.Read(ref _snapshot).InflightLimit;
            if (!TryAcquireInflightSlot(slotLimit))
            {
                throw new InvalidOperationException(
                    $"LFU cache keyspace '{Keyspace}' has reached its MaxInflight limit of {slotLimit} running factories.");
            }

            var slot = new InflightSlot(this);
            CacheEntry<TValue>? pendingEntry = null;
            var operation = new InflightOperation<TValue>(
                factoryCancellationToken => RunFactoryAndPublishAsync(
                    state,
                    key,
                    pendingEntry!,
                    factory,
                    expiry,
                    slot,
                    factoryCancellationToken),
                () => RemoveObserved(state, key, pendingEntry!));
            var createdTicks = _timeProvider.GetTimestamp();
            pendingEntry = CacheEntry<TValue>.Pending(
                operation,
                createdTicks,
                Volatile.Read(ref _snapshot).GetFrequencyEpoch(createdTicks));

            if (!state.Entries.TryAdd(key, pendingEntry))
            {
                slot.Dispose();
                operation.Dispose();
                continue;
            }

            Interlocked.Increment(ref state.Count);

            if (!ReferenceEquals(state, Volatile.Read(ref _state)))
            {
                RemoveObserved(state, key, pendingEntry);
                operation.AbandonOwner();
                slot.Dispose();
                continue;
            }

            try
            {
                RecordMiss();
                CheckWatermarks(state, Volatile.Read(ref _snapshot), pendingEntry: true);
            }
            catch
            {
                operation.AbandonOwner();
                slot.Dispose();
                throw;
            }

            return await operation.WaitForOwnerAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryGetLiveValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (!state.Entries.TryGetValue(key, out var entry))
            {
                value = default;
                return false;
            }

            if (!ReferenceEquals(state, Volatile.Read(ref _state)))
            {
                continue;
            }

            if (!entry.IsCompleted)
            {
                value = default;
                return false;
            }

            var nowTicks = _timeProvider.GetTimestamp();
            if (nowTicks >= Volatile.Read(ref entry.ExpiresAtTicks))
            {
                RemoveExpired(state, key, entry);
                value = default;
                return false;
            }

            entry.RecordAccess(Volatile.Read(ref _snapshot).GetFrequencyEpoch(nowTicks), nowTicks);
            RecordHit();
            value = entry.Value;
            return true;
        }
    }

    private async Task<TValue> RunFactoryAndPublishAsync(
        LfuCacheStoreState<TKey, TValue> state,
        TKey key,
        CacheEntry<TValue> pendingEntry,
        Func<TKey, CancellationToken, ValueTask<TValue>> factory,
        TimeSpan? expiry,
        InflightSlot slot,
        CancellationToken cancellationToken)
    {
        try
        {
            var value = await factory(key, cancellationToken).ConfigureAwait(false);
            var snapshot = Volatile.Read(ref _snapshot);
            var nowTicks = _timeProvider.GetTimestamp();
            var frequencyEpoch = snapshot.GetFrequencyEpoch(nowTicks);
            var completed = CacheEntry<TValue>.Completed(
                value,
                pendingEntry.GetFrequency(frequencyEpoch),
                frequencyEpoch,
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
        finally
        {
            slot.Dispose();
        }
    }

    private bool TryAcquireInflightSlot(int limit)
    {
        var count = Volatile.Read(ref _inflightCount);
        while (count < limit)
        {
            var observed = Interlocked.CompareExchange(ref _inflightCount, count + 1, count);
            if (observed == count)
            {
                return true;
            }

            count = observed;
        }

        return false;
    }

    private sealed class InflightSlot(LfuCache<TKey, TValue> owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Decrement(ref owner._inflightCount);
            }
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
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    LfuCacheLog.InvalidOptionsRejected(_logger, Keyspace, string.Join(" ", failures));
                }

                return;
            }

            var replacement = OptionsSnapshot.Create(options, _timeProvider, current);
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

            LfuCacheLog.OptionsReplaced(
                _logger,
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
            LfuCacheLog.OptionsApplyFailed(_logger, Keyspace, exception);
        }
    }

    private void CheckWatermarks(
        LfuCacheStoreState<TKey, TValue> state,
        OptionsSnapshot snapshot,
        bool pendingEntry = false)
    {
        var count = Volatile.Read(ref state.Count);
        var nowTicks = _timeProvider.GetTimestamp();
        if (count > snapshot.Capacity)
        {
            if (pendingEntry)
            {
                EnsureEvictionScheduled(nowTicks);
            }
            else
            {
                RequestEviction(nowTicks);
            }
        }

        if (count > snapshot.HardLimit
            && (!pendingEntry || nowTicks >= Volatile.Read(ref _nextEvictionTicks)))
        {
            EvictOneBatch(snapshot, nowTicks, true);
        }
    }

    private void RequestEviction()
    {
        RequestEviction(_timeProvider.GetTimestamp());
    }

    private void RequestEviction(long nowTicks)
    {
        ScheduleEviction(nowTicks);
    }

    private void EnsureEvictionScheduled(long nowTicks)
    {
        if (Interlocked.CompareExchange(ref _nextEvictionTicks, nowTicks, long.MaxValue) == long.MaxValue)
        {
            _registry.Signal();
        }
    }

    private void EvictOneBatch(OptionsSnapshot snapshot, long nowTicks, bool synchronous)
    {
        if (Interlocked.CompareExchange(ref _evictionGate, 1, 0) != 0)
        {
            ScheduleEvictionRetry(snapshot);
            return;
        }

        Interlocked.Exchange(ref _nextEvictionTicks, long.MaxValue);

        try
        {
            snapshot = Volatile.Read(ref _snapshot);
            var state = Volatile.Read(ref _state);
            if (Volatile.Read(ref state.Count) <= snapshot.Capacity)
            {
                CompleteEvictionCheck(madeProgress: false);
                return;
            }

            if (synchronous)
            {
                _metrics.SynchronousEviction(_metricTags);
                LfuCacheLog.SynchronousEvictionTriggered(
                    _logger,
                    Keyspace,
                    Volatile.Read(ref state.Count));
            }

            var stopwatch = Stopwatch.StartNew();
            var countBefore = Volatile.Read(ref state.Count);
            var batchSize = GetEvictionBatchSize(snapshot);
            var targetForBatch = Volatile.Read(ref _capacityContraction) != 0
                ? Math.Max(snapshot.Capacity, countBefore - batchSize)
                : snapshot.TargetLimit;
            var evicted = SelectAndRemoveCandidates(
                state,
                targetForBatch,
                nowTicks,
                snapshot.GetFrequencyEpoch(nowTicks),
                out var expired,
                out var minFrequency,
                out var maxFrequency);
            stopwatch.Stop();

            Interlocked.Increment(ref _evictionBatches);
            _metrics.EvictionBatch(stopwatch.Elapsed.TotalMilliseconds, _metricTags);

            if (evicted != 0)
            {
                Interlocked.Add(ref _evictions, evicted);
                _metrics.Evicted(evicted, _metricTags);
            }

            LfuCacheLog.EvictionCompleted(
                _logger,
                Keyspace,
                targetForBatch,
                evicted,
                expired,
                stopwatch.Elapsed.TotalMilliseconds,
                minFrequency,
                maxFrequency);

            CompleteEvictionCheck(expired != 0 || evicted != 0);
        }
        catch
        {
            ScheduleEvictionRetry(Volatile.Read(ref _snapshot));
            throw;
        }
        finally
        {
            // The queue belongs to the eviction gate. Always release retained keys and values, including on failure.
            _evictionCandidates.Clear();
            Volatile.Write(ref _evictionGate, 0);
        }
    }

    private int SelectAndRemoveCandidates(
        LfuCacheStoreState<TKey, TValue> state,
        long target,
        long nowTicks,
        uint frequencyEpoch,
        out int expired,
        out long minFrequency,
        out long maxFrequency)
    {
        var queue = _evictionCandidates;
        expired = 0;
        foreach (var pair in state.Entries)
        {
            var required = SaturatingInt(Math.Max(0, Volatile.Read(ref state.Count) - target));
            if (required == 0)
            {
                break;
            }

            var entry = pair.Value;
            if (!entry.IsCompleted)
            {
                continue;
            }

            if (nowTicks >= Volatile.Read(ref entry.ExpiresAtTicks))
            {
                if (RemoveExpired(state, pair.Key, entry))
                {
                    expired++;
                }

                continue;
            }

            while (queue.Count > required)
            {
                queue.Dequeue();
            }

            var priority = new EvictionPriority(
                entry.GetFrequency(frequencyEpoch),
                nowTicks - entry.CreatedTicks < _protectionWindowTicks,
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
                queue.DequeueEnqueue(candidate, priority);
            }
        }

        var remaining = SaturatingInt(Math.Max(0, Volatile.Read(ref state.Count) - target));
        while (queue.Count > remaining)
        {
            queue.Dequeue();
        }

        var removed = 0;
        minFrequency = long.MaxValue;
        maxFrequency = 0;
        while (Volatile.Read(ref state.Count) > target && queue.TryDequeue(out var candidate, out _))
        {
            if (!RemoveObserved(state, candidate.Key, candidate.Entry))
            {
                continue;
            }

            var frequency = candidate.Entry.GetFrequency(frequencyEpoch);
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

    private int ScanExpired(int budget, long nowTicks)
    {
        return Scan(
            ref _expirationCursor,
            budget,
            (state, pair) => pair.Value.IsCompleted
                && nowTicks >= Volatile.Read(ref pair.Value.ExpiresAtTicks)
                && RemoveExpired(state, pair.Key, pair.Value));
    }

    private int ScanForDecay(int budget, uint frequencyEpoch)
    {
        return Scan(
            ref _decayCursor,
            budget,
            (_, pair) => pair.Value.IsCompleted && pair.Value.Decay(frequencyEpoch));
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
        _hits.Increment();
        _metrics.Hit(_metricTags);
    }

    private void RecordMiss()
    {
        _misses.Increment();
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

    private void CompleteEvictionCheck(bool madeProgress)
    {
        var activeSnapshot = Volatile.Read(ref _snapshot);
        var state = Volatile.Read(ref _state);
        if (Volatile.Read(ref state.Count) > activeSnapshot.Capacity)
        {
            if (madeProgress)
            {
                RequestEviction();
            }
            else
            {
                ScheduleEvictionRetry(activeSnapshot);
            }
        }
        else
        {
            Volatile.Write(ref _capacityContraction, 0);
        }
    }

    private void ScheduleEvictionRetry(OptionsSnapshot snapshot)
    {
        ScheduleEviction(
            TimestampMath.Add(_timeProvider.GetTimestamp(), snapshot.MaintenanceIntervalTicks));
    }

    private void ScheduleEviction(long dueTicks)
    {
        var currentTicks = Volatile.Read(ref _nextEvictionTicks);
        while (dueTicks < currentTicks)
        {
            var observedTicks = Interlocked.CompareExchange(ref _nextEvictionTicks, dueTicks, currentTicks);
            if (observedTicks == currentTicks)
            {
                _registry.Signal();
                return;
            }

            currentTicks = observedTicks;
        }
    }

    private static void DisposeCursor(ref LfuCacheScanCursor<TKey, TValue>? cursor)
    {
        cursor?.Dispose();
        cursor = null;
    }
}
