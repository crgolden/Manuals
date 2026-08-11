namespace Manuals.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

public sealed class RedisHealthCheck : IHealthCheck
{
    private const int MaxAttempts = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly IConnectionMultiplexer _connectionMultiplexer;

    public RedisHealthCheck(IConnectionMultiplexer connectionMultiplexer)
    {
        _connectionMultiplexer = connectionMultiplexer;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        Exception lastException;
        var attempt = 0;
        do
        {
            attempt++;
            try
            {
                var database = _connectionMultiplexer.GetDatabase();
                var latency = await database.PingAsync();
                return HealthCheckResult.Healthy($"PONG in {latency.TotalMilliseconds:F0}ms");
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                }
            }
        }
        while (attempt < MaxAttempts);

        return HealthCheckResult.Unhealthy(lastException.Message, lastException);
    }
}
