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
    public async Task GetOrAddAsync_PendingKeysExceedCapacity_DoesNotStartDuplicateFactory()
    {
        using var host = new TestCacheHost<int, string>(
            configure: options =>
            {
                options.Capacity = 1;
                options.OverflowRatio = 0;
            });
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var duplicateStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFactoryCalls = 0;
        var secondFactoryCalls = 0;

        async ValueTask<string> Factory(int key, CancellationToken cancellationToken)
        {
            var calls = key == 1
                ? Interlocked.Increment(ref firstFactoryCalls)
                : Interlocked.Increment(ref secondFactoryCalls);
            if (calls > 1)
            {
                duplicateStarted.TrySetResult(key);
            }
            else if (key == 1)
            {
                firstStarted.TrySetResult(true);
            }
            else
            {
                secondStarted.TrySetResult(true);
            }

            await release.Task.WaitAsync(cancellationToken);
            return "value";
        }

        var first = host.Cache.GetOrAddAsync(1, Factory, null, TestContext.Current.CancellationToken).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        host.Clock.Advance(TimeSpan.FromSeconds(2));
        var second = host.Cache.GetOrAddAsync(2, Factory, null, TestContext.Current.CancellationToken).AsTask();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var third = host.Cache.GetOrAddAsync(1, Factory, null, TestContext.Current.CancellationToken).AsTask();
        try
        {
            Assert.False(duplicateStarted.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult(true);
        }

        await Task.WhenAll(first, second, third);
        Assert.Equal(1, firstFactoryCalls);
        Assert.Equal(1, secondFactoryCalls);
    }

    [Fact]
    public async Task GetOrAddAsync_FirstCallerCancellation_DoesNotCancelSharedFactoryForOtherCaller()
    {
        using var host = new TestCacheHost<int, string>();
        var factoryStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask<string> Factory(int _, CancellationToken cancellationToken)
        {
            factoryStarted.TrySetResult(cancellationToken);
            await release.Task.WaitAsync(cancellationToken);
            return "value";
        }

        using var firstCancellation = new CancellationTokenSource();
        var first = host.Cache.GetOrAddAsync(1, Factory, null, firstCancellation.Token).AsTask();
        var factoryToken = await factoryStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var second = host.Cache.GetOrAddAsync(1, Factory, null, TestContext.Current.CancellationToken).AsTask();

        firstCancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(factoryToken.IsCancellationRequested);

            release.TrySetResult(true);
            Assert.Equal(
                "value",
                await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    [Fact]
    public async Task GetOrAddAsync_OnlyWaiterCancellation_ClosesOperationForLaterCaller()
    {
        using var host = new TestCacheHost<int, string>();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancellationObserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;

        async ValueTask<string> Factory(int _, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref factoryCalls);
            if (call == 1)
            {
                using var registration = cancellationToken.Register(
                    () => firstCancellationObserved.TrySetResult(true));
                firstStarted.TrySetResult(true);
                try
                {
                    await releaseFirst.Task;
                    return "stale";
                }
                finally
                {
                    firstFinished.TrySetResult(true);
                }
            }

            secondStarted.TrySetResult(true);
            return "fresh";
        }

        using var firstCancellation = new CancellationTokenSource();
        var first = host.Cache.GetOrAddAsync(1, Factory, null, firstCancellation.Token).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        try
        {
            firstCancellation.Cancel();
            await firstCancellationObserved.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

            var second = host.Cache.GetOrAddAsync(
                1,
                Factory,
                null,
                TestContext.Current.CancellationToken).AsTask();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal("fresh", await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(2, factoryCalls);
        }
        finally
        {
            releaseFirst.TrySetResult(true);
            await firstFinished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task GetOrAddAsync_FactoryCancellationCallbackThrows_CallerObservesOperationCanceledException()
    {
        using var host = new TestCacheHost<int, string>();
        var factoryStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask<string> Factory(int _, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(
                () => throw new InvalidOperationException("factory cancellation callback failed"));
            factoryStarted.TrySetResult(true);
            try
            {
                await release.Task;
                return "value";
            }
            finally
            {
                factoryFinished.TrySetResult(true);
            }
        }

        using var callerCancellation = new CancellationTokenSource();
        var caller = host.Cache.GetOrAddAsync(1, Factory, null, callerCancellation.Token).AsTask();
        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        try
        {
            callerCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller);
        }
        finally
        {
            release.TrySetResult(true);
            await factoryFinished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task GetOrAddAsync_PendingEntriesAfterNoCandidateScan_DoesNotRepeatSynchronousEviction()
    {
        using var host = new TestCacheHost<int, string>(
            configure: options =>
            {
                options.Capacity = 1;
                options.OverflowRatio = 0;
            });
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = new List<Task<string>>();

        async ValueTask<string> Factory(int key, CancellationToken cancellationToken)
        {
            switch (key)
            {
                case 1:
                    firstStarted.TrySetResult(true);
                    break;
                case 2:
                    secondStarted.TrySetResult(true);
                    break;
                case 3:
                    thirdStarted.TrySetResult(true);
                    break;
            }

            await release.Task.WaitAsync(cancellationToken);
            return $"value-{key}";
        }

        try
        {
            callers.Add(host.Cache.GetOrAddAsync(1, Factory, null, TestContext.Current.CancellationToken).AsTask());
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            callers.Add(host.Cache.GetOrAddAsync(2, Factory, null, TestContext.Current.CancellationToken).AsTask());
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            callers.Add(host.Cache.GetOrAddAsync(3, Factory, null, TestContext.Current.CancellationToken).AsTask());
            await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(1, host.Cache.GetStats().EvictionBatches);
        }
        finally
        {
            release.TrySetResult(true);
            await Task.WhenAll(callers);
        }
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

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
                await host.Cache.GetOrAddAsync(1, (_, _) =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    return ValueTask.FromException<string>(new InvalidOperationException("factory failed"));
                }, null, TestContext.Current.CancellationToken));

        var value = await host.Cache.GetOrAddAsync(1, (_, _) =>
        {
            Interlocked.Increment(ref factoryCalls);
            return new ValueTask<string>("recovered");
        }, null, TestContext.Current.CancellationToken);

        Assert.Equal("recovered", value);
        Assert.Equal(2, factoryCalls);
        Assert.Equal(1, host.Cache.Count);
    }
}
