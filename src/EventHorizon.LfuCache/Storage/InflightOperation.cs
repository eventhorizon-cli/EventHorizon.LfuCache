namespace EventHorizon.LfuCache.Storage;

internal sealed class InflightOperation<TValue> : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _factoryCancellation = new();
    private readonly Action _abandon;
    private Func<CancellationToken, Task<TValue>>? _factory;
    private bool _acceptingWaiters = true;
    private bool _cancellationInProgress;
    private bool _disposeRequested;
    private bool _disposed;
    private int _waiterCount = 1;

    public InflightOperation(Func<CancellationToken, Task<TValue>> factory, Action abandon)
    {
        _factory = factory;
        _abandon = abandon;
        Task = new Lazy<Task<TValue>>(
            () => RunFactoryAsync(Interlocked.Exchange(ref _factory, null)!),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private Lazy<Task<TValue>> Task { get; }

    public Task<TValue> WaitForOwnerAsync(CancellationToken cancellationToken)
    {
        return WaitAsync(Task.Value, cancellationToken);
    }

    public Task<TValue>? TryWaitAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_acceptingWaiters)
            {
                return null;
            }

            _waiterCount++;
        }

        return WaitAsync(Task.Value, cancellationToken);
    }

    public void AbandonOwner()
    {
        bool disposeUnused;

        lock (_gate)
        {
            _waiterCount--;
            _acceptingWaiters = false;
            disposeUnused = _waiterCount == 0 && !Task.IsValueCreated;
        }

        try
        {
            _abandon();
        }
        finally
        {
            if (disposeUnused)
            {
                Dispose();
            }
            else
            {
                CancelFactory();
            }
        }
    }

    public void Dispose()
    {
        var dispose = false;

        lock (_gate)
        {
            if (_disposed || _disposeRequested)
            {
                return;
            }

            _acceptingWaiters = false;
            if (_cancellationInProgress)
            {
                _disposeRequested = true;
            }
            else
            {
                _disposed = true;
                dispose = true;
            }
        }

        if (dispose)
        {
            _factoryCancellation.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async Task<TValue> WaitAsync(Task<TValue> task, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var abandon = false;

            lock (_gate)
            {
                _waiterCount--;
                if (_waiterCount == 0 && !task.IsCompleted && _acceptingWaiters)
                {
                    _acceptingWaiters = false;
                    abandon = true;
                }
            }

            if (abandon)
            {
                try
                {
                    _abandon();
                }
                finally
                {
                    CancelFactory();
                }
            }
        }
    }

    private void CancelFactory()
    {
        lock (_gate)
        {
            if (_disposed || _cancellationInProgress)
            {
                return;
            }

            _cancellationInProgress = true;
        }

        try
        {
            _factoryCancellation.Cancel();
        }
        catch (AggregateException)
        {
            // Factory cancellation callbacks cannot replace a caller's cancellation result.
        }
        catch (ObjectDisposedException)
        {
            // The factory completed immediately before cancellation began.
        }
        finally
        {
            var dispose = false;

            lock (_gate)
            {
                _cancellationInProgress = false;
                if (_disposeRequested && !_disposed)
                {
                    _disposed = true;
                    dispose = true;
                }
            }

            if (dispose)
            {
                _factoryCancellation.Dispose();
            }
        }
    }

    private async Task<TValue> RunFactoryAsync(Func<CancellationToken, Task<TValue>> factory)
    {
        try
        {
            return await factory(_factoryCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Dispose();
        }
    }
}
