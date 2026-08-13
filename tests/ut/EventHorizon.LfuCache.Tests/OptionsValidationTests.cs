using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EventHorizon.LfuCache.Tests;

public sealed class OptionsValidationTests
{
    [Fact]
    public void AddLfuCache_InvalidOptions_ThrowsOptionsValidationExceptionWhenResolved()
    {
        var services = new ServiceCollection();
        services.AddLfuCache<int, string>("invalid", options =>
        {
            options.Capacity = 0;
            options.EvictionRatio = double.NaN;
            options.OverflowRatio = 0.6;
            options.DefaultExpiry = TimeSpan.Zero;
            options.MaintenanceInterval = TimeSpan.FromMilliseconds(500);
            options.DecayInterval = TimeSpan.FromHours(25);
        });
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredKeyedService<ILfuCache<int, string>>("invalid"));

        Assert.Contains(exception.Failures, failure => failure.Contains("Capacity"));
        Assert.Contains(exception.Failures, failure => failure.Contains("EvictionRatio"));
        Assert.Contains(exception.Failures, failure => failure.Contains("OverflowRatio"));
        Assert.Contains(exception.Failures, failure => failure.Contains("DefaultExpiry"));
        Assert.Contains(exception.Failures, failure => failure.Contains("MaintenanceInterval"));
        Assert.Contains(exception.Failures, failure => failure.Contains("DecayInterval"));
    }

    [Fact]
    public void Set_NegativeExplicitExpiry_ThrowsAndZeroDisablesDefaultExpiry()
    {
        using var host = new TestCacheHost<int, string>(
            configure: options => options.DefaultExpiry = TimeSpan.FromSeconds(1));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => host.Cache.Set(1, "value", TimeSpan.FromSeconds(-1)));

        host.Cache.Set(1, "value", TimeSpan.Zero);
        host.Clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(host.Cache.TryGet(1, out var value));
        Assert.Equal("value", value);
    }
}
