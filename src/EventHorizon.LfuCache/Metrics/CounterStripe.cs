using System.Runtime.InteropServices;

namespace EventHorizon.LfuCache.Metrics;

[StructLayout(LayoutKind.Explicit, Size = 128)]
internal sealed class CounterStripe
{
    [FieldOffset(0)]
    private long _value;

    internal void Increment()
    {
        Interlocked.Increment(ref _value);
    }

    internal long Read()
    {
        return Volatile.Read(ref _value);
    }
}
