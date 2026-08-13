namespace EventHorizon.LfuCache.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task GetOrAdd_ConcurrentCallers_InvokeFactoryOnce()
    {
        using var host = new TestCacheHost<int, string>();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;

        var callers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => host.Cache.GetOrAdd(7, _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                started.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
                return "value";
            })))
            .ToArray();

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        release.SetResult(true);
        var values = await Task.WhenAll(callers);

        Assert.Equal(1, factoryCalls);
        Assert.All(values, value => Assert.Equal("value", value));
        Assert.Equal(1, host.Cache.Count);
    }

    [Fact]
    public async Task GetOrAddAsync_ConcurrentCallers_InvokeFactoryOnce()
    {
        using var host = new TestCacheHost<int, string>();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;

        async ValueTask<string> Factory(int _, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref factoryCalls);
            started.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken);
            return "value";
        }

        var callers = Enumerable.Range(0, 8)
            .Select(_ => host.Cache.GetOrAddAsync(7, Factory).AsTask())
            .ToArray();

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        release.SetResult(true);
        var values = await Task.WhenAll(callers);

        Assert.Equal(1, factoryCalls);
        Assert.All(values, value => Assert.Equal("value", value));
        Assert.Equal(1, host.Cache.Count);
    }

    [Fact]
    public void GetOrAdd_FactoryFailure_RemovesPendingEntry()
    {
        using var host = new TestCacheHost<int, string>();
        var factoryCalls = 0;

        Assert.Throws<InvalidOperationException>(() => host.Cache.GetOrAdd(1, _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            throw new InvalidOperationException("factory failed");
        }));

        var value = host.Cache.GetOrAdd(1, _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            return "recovered";
        });

        Assert.Equal("recovered", value);
        Assert.Equal(2, factoryCalls);
        Assert.Equal(1, host.Cache.Count);
    }

    [Fact]
    public async Task GetOrAddAsync_FactoryFailure_RemovesPendingEntry()
    {
        using var host = new TestCacheHost<int, string>();
        var factoryCalls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await host.Cache.GetOrAddAsync(1, (_, _) =>
            {
                Interlocked.Increment(ref factoryCalls);
                return ValueTask.FromException<string>(new InvalidOperationException("factory failed"));
            }, ct: TestContext.Current.CancellationToken));

        var value = await host.Cache.GetOrAddAsync(1, (_, _) =>
        {
            Interlocked.Increment(ref factoryCalls);
            return new ValueTask<string>("recovered");
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("recovered", value);
        Assert.Equal(2, factoryCalls);
        Assert.Equal(1, host.Cache.Count);
    }
}
