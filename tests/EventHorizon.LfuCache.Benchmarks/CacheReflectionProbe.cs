using System.Collections;
using System.Globalization;
using System.Reflection;

namespace EventHorizon.LfuCache.Benchmarks;

internal static class CacheReflectionProbe
{
    private const BindingFlags _instanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static Action<long> CreateMaintenanceDelegate(object cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        var method = cache.GetType().GetMethod("RunMaintenance", _instanceFlags, [typeof(long)]);
        if (method is null)
        {
            throw new InvalidOperationException(
                $"Unable to locate RunMaintenance(long) on {cache.GetType().FullName}.");
        }

        return method.CreateDelegate<Action<long>>(cache);
    }

    public static FrequencyObservation ReadFrequencies(object cache, long nowTicks)
    {
        ArgumentNullException.ThrowIfNull(cache);

        try
        {
            var state = GetMemberValue(cache, "_state")
                ?? throw new InvalidOperationException("The cache state field is unavailable.");
            var entries = GetMemberValue(state, "Entries") as IEnumerable
                ?? throw new InvalidOperationException("The cache entries collection is unavailable.");
            var snapshot = GetMemberValue(cache, "_snapshot");
            var epoch = GetFrequencyEpoch(snapshot, nowTicks);
            var frequencies = new SortedDictionary<long, int>();
            var entryCount = 0;
            var frequencyAccessor = default(Func<object, long>);

            foreach (var item in entries)
            {
                if (item is null)
                {
                    continue;
                }

                var entry = GetMemberValue(item, "Value");
                if (entry is null)
                {
                    continue;
                }

                frequencyAccessor ??= CreateFrequencyAccessor(entry, snapshot, epoch);
                var frequency = frequencyAccessor(entry);
                frequencies.TryGetValue(frequency, out var count);
                frequencies[frequency] = count + 1;
                entryCount++;
            }

            return new FrequencyObservation(true, null, entryCount, frequencies);
        }
        catch (Exception exception) when (exception is MissingMemberException or InvalidOperationException
            or TargetInvocationException)
        {
            return new FrequencyObservation(
                false,
                exception.GetBaseException().Message,
                0,
                new SortedDictionary<long, int>());
        }
    }

    private static Func<object, long> CreateFrequencyAccessor(object entry, object? snapshot, uint epoch)
    {
        var entryType = entry.GetType();
        var frequencyField = entryType.GetField("Frequency", _instanceFlags);
        if (frequencyField is not null)
        {
            return current => Convert.ToInt64(frequencyField.GetValue(current), CultureInfo.InvariantCulture);
        }

        var frequencyProperty = entryType.GetProperty("Frequency", _instanceFlags);
        if (frequencyProperty is not null)
        {
            return current => Convert.ToInt64(frequencyProperty.GetValue(current), CultureInfo.InvariantCulture);
        }

        var getFrequency = entryType.GetMethod("GetFrequency", _instanceFlags);
        if (getFrequency is null || getFrequency.GetParameters().Length != 1)
        {
            throw new MissingMemberException(entryType.FullName, "Frequency/GetFrequency");
        }

        var parameterType = getFrequency.GetParameters()[0].ParameterType;
        var argument = parameterType == typeof(uint)
            ? (object)epoch
            : parameterType == typeof(int)
                ? checked((object)(int)epoch)
                : parameterType == typeof(long)
                    ? (object)(long)epoch
                    : throw new MissingMemberException(
                        entryType.FullName,
                        $"GetFrequency({parameterType.Name})");

        return current => Convert.ToInt64(
            getFrequency.Invoke(current, [argument]),
            CultureInfo.InvariantCulture);
    }

    private static uint GetFrequencyEpoch(object? snapshot, long nowTicks)
    {
        if (snapshot is null)
        {
            return 0;
        }

        var method = snapshot.GetType().GetMethod("GetFrequencyEpoch", _instanceFlags);
        if (method is null || method.GetParameters().Length != 1)
        {
            return 0;
        }

        var parameterType = method.GetParameters()[0].ParameterType;
        var argument = parameterType == typeof(long)
            ? (object)nowTicks
            : parameterType == typeof(ulong)
                ? checked((object)(ulong)nowTicks)
                : parameterType == typeof(int)
                    ? checked((object)(int)nowTicks)
                    : throw new MissingMemberException(snapshot.GetType().FullName, "GetFrequencyEpoch");

        return Convert.ToUInt32(method.Invoke(snapshot, [argument]), CultureInfo.InvariantCulture);
    }

    private static object? GetMemberValue(object instance, string name)
    {
        var type = instance.GetType();
        var field = type.GetField(name, _instanceFlags);
        if (field is not null)
        {
            return field.GetValue(instance);
        }

        var property = type.GetProperty(name, _instanceFlags);
        return property?.GetValue(instance);
    }

    internal sealed record FrequencyObservation(
        bool Supported,
        string? Error,
        int EntryCount,
        IReadOnlyDictionary<long, int> CountsByFrequency);
}
