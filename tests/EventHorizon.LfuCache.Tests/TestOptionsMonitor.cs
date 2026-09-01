using Microsoft.Extensions.Options;

namespace EventHorizon.LfuCache.Tests;

internal sealed class TestOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
{
    private readonly object _gate = new();
    private readonly List<Action<TOptions, string?>> _listeners = [];
    private TOptions _currentValue;

    public TestOptionsMonitor(TOptions initialValue)
    {
        _currentValue = initialValue;
    }

    public TOptions CurrentValue => Get(Options.DefaultName);

    public TOptions Get(string? name)
    {
        lock (_gate)
        {
            return _currentValue;
        }
    }

    public IDisposable? OnChange(Action<TOptions, string?> listener)
    {
        lock (_gate)
        {
            _listeners.Add(listener);
        }

        return new CallbackRegistration(() => Remove(listener));
    }

    public void Update(TOptions value, string? name)
    {
        Action<TOptions, string?>[] listeners;
        lock (_gate)
        {
            _currentValue = value;
            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
        {
            listener(value, name);
        }
    }

    private void Remove(Action<TOptions, string?> listener)
    {
        lock (_gate)
        {
            _listeners.Remove(listener);
        }
    }

    private sealed class CallbackRegistration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
