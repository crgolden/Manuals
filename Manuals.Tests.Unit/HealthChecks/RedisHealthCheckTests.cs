namespace Manuals.Tests.Unit.HealthChecks;

using Manuals.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using StackExchange.Redis;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class RedisHealthCheckTests
{
    private static readonly int PingLatencyMilliseconds = TestValues.NewLatencyMilliseconds();
    private static readonly TimeSpan PingLatency = TimeSpan.FromMilliseconds(PingLatencyMilliseconds);

    [Fact]
    public async Task CheckHealthAsync_ReturnsHealthy_WhenPingSucceeds()
    {
        // Arrange
        var database = new Mock<IDatabase>(MockBehavior.Strict);
        database.Setup(d => d.PingAsync(It.IsAny<CommandFlags>())).ReturnsAsync(PingLatency);
        var healthCheck = new RedisHealthCheck(CreateMultiplexer(database).Object);

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal($"PONG in {PingLatencyMilliseconds}ms", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsUnhealthyWithLastException_WhenRedisIsUnreachable()
    {
        // Arrange
        var expected = new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, TestValues.NewFailureReason());
        var database = new Mock<IDatabase>(MockBehavior.Strict);
        database.Setup(d => d.PingAsync(It.IsAny<CommandFlags>())).ThrowsAsync(expected);
        var healthCheck = new RedisHealthCheck(CreateMultiplexer(database).Object);

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(expected.Message, result.Description);
        Assert.Same(expected, result.Exception);
    }

    [Fact]
    public async Task CheckHealthAsync_RetriesOnce_BeforeReportingUnhealthy()
    {
        // Arrange
        var database = new Mock<IDatabase>(MockBehavior.Strict);
        database
            .Setup(d => d.PingAsync(It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, TestValues.NewFailureReason()));
        var healthCheck = new RedisHealthCheck(CreateMultiplexer(database).Object);

        // Act
        await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        // Assert
        database.Verify(d => d.PingAsync(It.IsAny<CommandFlags>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsHealthy_WhenFirstAttemptFailsAndRetrySucceeds()
    {
        // Arrange
        var database = new Mock<IDatabase>(MockBehavior.Strict);
        database
            .SetupSequence(d => d.PingAsync(It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, TestValues.NewFailureReason()))
            .ReturnsAsync(PingLatency);
        var healthCheck = new RedisHealthCheck(CreateMultiplexer(database).Object);

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private static Mock<IConnectionMultiplexer> CreateMultiplexer(Mock<IDatabase> database)
    {
        var multiplexer = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        multiplexer
            .Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(database.Object);
        return multiplexer;
    }
}
