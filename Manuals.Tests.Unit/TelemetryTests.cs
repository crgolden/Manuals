namespace Manuals.Tests.Unit;

using System.Diagnostics;

[Trait("Category", "Unit")]
public sealed class TelemetryTests : IDisposable
{
    private readonly Telemetry _telemetry = new();

    [Fact]
    public void StartActivity_AttachesToTheRequestThatCausedIt()
    {
        // Arrange
        using var listener = ListenToManuals();
        using var request = new ActivitySource(Telemetry.SourceName)
            .StartActivity(Generated.NewRequestActivityName(), ActivityKind.Server);

        // Act
        using var work = _telemetry.StartActivity(Generated.NewWorkActivityName());

        // Assert
        Assert.Equal(request?.SpanId, work?.ParentSpanId);
    }

    [Fact]
    public void StartActivity_SharesTheTraceIdOfTheRequestThatCausedIt()
    {
        // Arrange
        using var listener = ListenToManuals();
        using var request = new ActivitySource(Telemetry.SourceName)
            .StartActivity(Generated.NewRequestActivityName(), ActivityKind.Server);

        // Act
        using var work = _telemetry.StartActivity(Generated.NewWorkActivityName());

        // Assert
        Assert.Equal(request?.TraceId, work?.TraceId);
    }

    [Fact]
    public void StartActivity_StillProducesASpanWhenNothingIsInFlight()
    {
        // Arrange
        using var listener = ListenToManuals();

        // Act
        using var work = _telemetry.StartActivity(Generated.NewWorkActivityName());

        // Assert
        Assert.NotNull(work);
    }

    [Fact]
    public void StartActivity_IsARootWhenNothingIsInFlight()
    {
        // Arrange
        using var listener = ListenToManuals();

        // Act
        using var work = _telemetry.StartActivity(Generated.NewWorkActivityName());

        // Assert
        Assert.Null(work?.Parent);
    }

    public void Dispose() => _telemetry.Dispose();

    private static ActivityListener ListenToManuals()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, Telemetry.SourceName, StringComparison.Ordinal),
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
