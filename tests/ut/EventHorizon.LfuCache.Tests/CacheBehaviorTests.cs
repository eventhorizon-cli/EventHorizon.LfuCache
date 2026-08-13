using Microsoft.Extensions.DependencyInjection;

namespace EventHorizon.LfuCache.Tests;

public sealed class CacheBehaviorTests
{
    [Fact]
    public async Task DynamicCache_ForwardsMutationsAndReads_ToRegisteredTypedStore()
    {
        using var host = new TestCacheHost<int, string>("forward");
        var dynamic = host.Services.GetRequiredKeyedService<ILfuCache>("forward");
        var factoryCalled = false;

        dynamic.Set<int, string>(1, "one");
        Assert.True(host.Cache.TryGet(1, out var typedValue));
        Assert.Equal("one", typedValue);

        var existing = dynamic.GetOrAdd<int, string>(1, _ =>
        {
            factoryCalled = true;
            return "replacement";
        });
        Assert.Equal("one", existing);
        Assert.False(factoryCalled);

        var added = await dynamic.GetOrAddAsync<int, string>(2, static (_, _) =>
            new ValueTask<string>("two"), ct: TestContext.Current.CancellationToken);
        Assert.Equal("two", added);
        Assert.True(dynamic.TryGet<int, string>(2, out var dynamicValue));
        Assert.Equal("two", dynamicValue);

        Assert.True(dynamic.Remove<int, string>(1));
        dynamic.Clear();
        Assert.Equal(0, dynamic.GetStats().Count);
    }

    [Fact]
    public void DynamicCache_TypeMismatch_ThrowsDescriptiveException()
    {
        using var host = new TestCacheHost<int, string>("typed");
        var dynamic = host.Services.GetRequiredKeyedService<ILfuCache>("typed");

        var exception = Assert.Throws<InvalidOperationException>(
            () => dynamic.TryGet<string, string>("key", out _));

        Assert.Contains("typed", exception.Message);
        Assert.Contains("Int32", exception.Message);
        Assert.Contains("String", exception.Message);
    }

    [Fact]
    public void Set_NullValue_IsAHitAndRemainsStored()
    {
        using var host = new TestCacheHost<int, string?>();

        host.Cache.Set(1, null);

        var found = host.Cache.TryGet(1, out var value);

        Assert.True(found);
        Assert.Null(value);
        Assert.Equal(1, host.Cache.Count);
        Assert.Equal(1, host.Cache.GetStats().Hits);
    }

    [Fact]
    public void Set_ExplicitAndDefaultExpiry_ExpireEntriesOnRead()
    {
        using var host = new TestCacheHost<int, string>(
            configure: options => options.DefaultExpiry = TimeSpan.FromSeconds(5));

        host.Cache.Set(1, "explicit", TimeSpan.FromSeconds(3));
        host.Cache.Set(2, "default");
        host.Clock.Advance(TimeSpan.FromSeconds(4));

        Assert.False(host.Cache.TryGet(1, out _));
        Assert.True(host.Cache.TryGet(2, out var beforeDefaultExpiry));
        Assert.Equal("default", beforeDefaultExpiry);

        host.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(host.Cache.TryGet(2, out _));
        Assert.Equal(0, host.Cache.Count);
        Assert.Equal(2, host.Cache.GetStats().Expirations);
    }

    [Fact]
    public void RemoveAndClear_RemoveEntriesAndReportExpectedPresence()
    {
        using var host = new TestCacheHost<int, string>();
        host.Cache.Set(1, "one");
        host.Cache.Set(2, "two");

        Assert.True(host.Cache.Remove(1));
        Assert.False(host.Cache.Remove(1));
        Assert.Equal(1, host.Cache.Count);

        host.Cache.Clear();

        Assert.Equal(0, host.Cache.Count);
        Assert.False(host.Cache.TryGet(2, out _));
    }
}
