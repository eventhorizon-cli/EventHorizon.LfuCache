using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EventHorizon.LfuCache.Tests;

public sealed class HostingTests
{
    [Fact]
    public async Task StartAsync_InvalidRegisteredOptions_FailsBeforeServingTraffic()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLfuCache<int, string>("invalid", options => options.Capacity = 0);
        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_UnresolvedCache_EagerlyInitializesDynamicFacade()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLfuCache<int, string>("eager");
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var dynamic = host.Services.GetRequiredKeyedService<ILfuCache>("EAGER");
            dynamic.Set<int, string>(1, "value");

            Assert.True(dynamic.TryGet<int, string>(1, out var value));
            Assert.Equal("value", value);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
