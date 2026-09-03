using OpenTelemetry.Metrics;

namespace EventHorizon.LfuCache.Tests;

public sealed class OpenTelemetryRegistrationTests
{
    [Fact]
    public void AddLfuCacheInstrumentation_BuilderProvided_RegistersMeterAndReturnsBuilder()
    {
        var builder = new RecordingMeterProviderBuilder();

        var result = builder.AddLfuCacheInstrumentation();

        Assert.Same(builder, result);
        Assert.Equal(["EventHorizon.LfuCache"], builder.MeterNames);
    }

    [Fact]
    public void AddLfuCacheInstrumentation_NullBuilder_Throws()
    {
        MeterProviderBuilder builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() => builder.AddLfuCacheInstrumentation());

        Assert.Equal("builder", exception.ParamName);
    }

    private sealed class RecordingMeterProviderBuilder : MeterProviderBuilder
    {
        public List<string> MeterNames { get; } = [];

        public override MeterProviderBuilder AddInstrumentation<TInstrumentation>(
            Func<TInstrumentation> instrumentationFactory)
        {
            return this;
        }

        public override MeterProviderBuilder AddMeter(params string[] names)
        {
            MeterNames.AddRange(names);
            return this;
        }
    }
}
