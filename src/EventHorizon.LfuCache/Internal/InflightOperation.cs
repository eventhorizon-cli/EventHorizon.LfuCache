namespace EventHorizon.LfuCache.Internal;

internal sealed class InflightOperation<TValue>
{
    private Func<Task<TValue>>? _factory;

    public InflightOperation(Func<Task<TValue>> factory)
    {
        _factory = factory;
        Task = new Lazy<Task<TValue>>(
            () => Interlocked.Exchange(ref _factory, null)!(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Lazy<Task<TValue>> Task { get; }
}
