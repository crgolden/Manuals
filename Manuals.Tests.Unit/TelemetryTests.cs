namespace Manuals.Tests.Unit;

using System.Diagnostics;

[Trait("Category", "Unit")]
public sealed class TelemetryTests
{
    [Fact]
    public void StartActivity_AttachesToTheRequestThatCausedIt()
    {
        // Arrange
        using var listener = ListenToManuals();
        using var request = new ActivitySource(nameof(Manuals)).StartActivity("GET /chats", ActivityKind.Server);

        // Act
        using var work = Telemetry.StartActivity("manuals.openai.complete_chat");

        // Assert
        Assert.Equal(request?.SpanId, work?.ParentSpanId);
    }

    [Fact]
    public void StartActivity_SharesTheTraceIdOfTheRequestThatCausedIt()
    {
        // Arrange
        using var listener = ListenToManuals();
        using var request = new ActivitySource(nameof(Manuals)).StartActivity("GET /chats", ActivityKind.Server);

        // Act
        using var work = Telemetry.StartActivity("manuals.chat.list");

        // Assert
        Assert.Equal(request?.TraceId, work?.TraceId);
    }

    [Fact]
    public void StartActivity_StillProducesASpanWhenNothingIsInFlight()
    {
        // Arrange
        using var listener = ListenToManuals();

        // Act
        using var work = Telemetry.StartActivity("manuals.chat.list");

        // Assert
        Assert.NotNull(work);
    }

    [Fact]
    public void StartActivity_IsARootWhenNothingIsInFlight()
    {
        // Arrange
        using var listener = ListenToManuals();

        // Act
        using var work = Telemetry.StartActivity("manuals.chat.list");

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
