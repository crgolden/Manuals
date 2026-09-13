namespace Manuals.Tests.Unit;

using System.Diagnostics;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class TelemetryTests
{
    [Fact]
    public void StartActivity_AttachesToTheRequestThatCausedIt()
    {
        // Arrange
        using var listener = ListenToManuals();
        using var request = new ActivitySource(nameof(Manuals))
            .StartActivity(TestValues.NewRequestActivityName(), ActivityKind.Server);

        // Act
        using var work = Telemetry.StartActivity(TestValues.NewWorkActivityName());

        // Assert
        Assert.Equal(request?.SpanId, work?.ParentSpanId);
    }

    [Fact]
    public void StartActivity_SharesTheTraceIdOfTheRequestThatCausedIt()
    {
        // Arrange
        using var listener = ListenToManuals();
        using var request = new ActivitySource(nameof(Manuals))
            .StartActivity(TestValues.NewRequestActivityName(), ActivityKind.Server);

        // Act
        using var work = Telemetry.StartActivity(TestValues.NewWorkActivityName());

        // Assert
        Assert.Equal(request?.TraceId, work?.TraceId);
    }

    [Fact]
    public void StartActivity_StillProducesASpanWhenNothingIsInFlight()
    {
        // Arrange
        using var listener = ListenToManuals();

        // Act
        using var work = Telemetry.StartActivity(TestValues.NewWorkActivityName());

        // Assert
        Assert.NotNull(work);
    }

    [Fact]
    public void StartActivity_IsARootWhenNothingIsInFlight()
    {
        // Arrange
        using var listener = ListenToManuals();

        // Act
        using var work = Telemetry.StartActivity(TestValues.NewWorkActivityName());

        // Assert
        Assert.Null(work?.Parent);
    }

    private static ActivityListener ListenToManuals()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, nameof(Manuals), StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
