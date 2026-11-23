namespace Manuals.Tests.Unit.Logging;

using Manuals.Logging;
using Microsoft.AspNetCore.Http;
using Serilog.Events;

[Trait("Category", "Unit")]
public sealed class RequestLogLevelTests
{
    [Fact]
    public void Of_ACancellationOnARequestTheClientAborted_IsWarning()
    {
        // Arrange
        var httpContext = AbortedRequest();
        var elapsedMilliseconds = Generated.NewLatencyMilliseconds();

        // Act
        var level = RequestLogLevel.Of(httpContext, elapsedMilliseconds, new OperationCanceledException());

        // Assert
        Assert.Equal(LogEventLevel.Warning, level);
    }

    [Fact]
    public void Of_ATaskCancellationOnARequestTheClientAborted_IsWarning()
    {
        // Arrange
        var httpContext = AbortedRequest();
        var elapsedMilliseconds = Generated.NewLatencyMilliseconds();

        // Act
        var level = RequestLogLevel.Of(httpContext, elapsedMilliseconds, new TaskCanceledException());

        // Assert
        Assert.Equal(LogEventLevel.Warning, level);
    }

    [Fact]
    public void Of_ACancellationTheClientDidNotCause_IsError()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        var elapsedMilliseconds = Generated.NewLatencyMilliseconds();

        // Act
        var level = RequestLogLevel.Of(httpContext, elapsedMilliseconds, new OperationCanceledException());

        // Assert
        Assert.Equal(LogEventLevel.Error, level);
    }

    [Fact]
    public void Of_AnotherExceptionOnARequestTheClientAborted_IsError()
    {
        // Arrange
        var httpContext = AbortedRequest();
        var elapsedMilliseconds = Generated.NewLatencyMilliseconds();

        // Act
        var level = RequestLogLevel.Of(httpContext, elapsedMilliseconds, new InvalidOperationException());

        // Assert
        Assert.Equal(LogEventLevel.Error, level);
    }

    [Fact]
    public void Of_AServerErrorStatusWithNoException_IsError()
    {
        // Arrange
        var httpContext = RequestEndingWith((int)Generated.NewServerErrorStatusCode());
        var elapsedMilliseconds = Generated.NewLatencyMilliseconds();

        // Act
        var level = RequestLogLevel.Of(httpContext, elapsedMilliseconds, exception: null);

        // Assert
        Assert.Equal(LogEventLevel.Error, level);
    }

    [Fact]
    public void Of_TheFirstServerErrorStatus_IsError()
    {
        // Arrange
        var httpContext = RequestEndingWith(StatusCodes.Status500InternalServerError);
        var elapsedMilliseconds = Generated.NewLatencyMilliseconds();

        // Act
        var level = RequestLogLevel.Of(httpContext, elapsedMilliseconds, exception: null);

        // Assert
        Assert.Equal(LogEventLevel.Error, level);
    }

    [Fact]
    public void Of_AClientErrorStatusWithNoException_IsInformation()
    {
        // Arrange
        var httpContext = RequestEndingWith((int)Generated.NewClientErrorStatusCode());
        var elapsedMilliseconds = Generated.NewLatencyMilliseconds();

        // Act
        var level = RequestLogLevel.Of(httpContext, elapsedMilliseconds, exception: null);

        // Assert
        Assert.Equal(LogEventLevel.Information, level);
    }

    [Fact]
    public void Of_TheLastStatusBelowServerErrors_IsInformation()
    {
        // Arrange
        var httpContext = RequestEndingWith(StatusCodes.Status499ClientClosedRequest);
        var elapsedMilliseconds = Generated.NewLatencyMilliseconds();

        // Act
        var level = RequestLogLevel.Of(httpContext, elapsedMilliseconds, exception: null);

        // Assert
        Assert.Equal(LogEventLevel.Information, level);
    }

    private static DefaultHttpContext AbortedRequest() =>
        new() { RequestAborted = new CancellationToken(canceled: true) };

    private static DefaultHttpContext RequestEndingWith(int statusCode)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.StatusCode = statusCode;
        return httpContext;
    }
}
