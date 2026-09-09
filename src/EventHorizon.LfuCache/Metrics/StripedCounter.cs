namespace EventHorizon.LfuCache.Metrics;

internal sealed class StripedCounter
{
    private const int _maximumStripeCount = 32;

    private readonly CounterStripe[] _stripes;
    private readonly int _stripeMask;

    public StripedCounter()
        : this(SelectStripeCount(Environment.ProcessorCount))
    {
    }

    internal StripedCounter(int stripeCount)
    {
        var normalizedStripeCount = NormalizeStripeCount(stripeCount);
        _stripes = new CounterStripe[normalizedStripeCount];
        _stripeMask = normalizedStripeCount - 1;

        for (var index = 0; index < _stripes.Length; index++)
        {
            _stripes[index] = new CounterStripe();
        }
    }

    public void Increment()
    {
        _stripes[GetStripeIndex()].Increment();
    }

    public long Read()
    {
        var total = 0L;
        foreach (var stripe in _stripes)
        {
            total += stripe.Read();
        }

        return total;
    }

    private int GetStripeIndex()
    {
        return Environment.CurrentManagedThreadId & _stripeMask;
    }

    private static int SelectStripeCount(int processorCount)
    {
        var stripeCount = 1;
        while (stripeCount < processorCount && stripeCount < _maximumStripeCount)
        {
            stripeCount <<= 1;
        }

        return stripeCount;
    }

    private static int NormalizeStripeCount(int stripeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripeCount);

        return SelectStripeCount(Math.Min(stripeCount, _maximumStripeCount));
    }
}
