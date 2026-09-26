namespace Manuals.Tests.Integration;

using System.Globalization;
using Manuals.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Configuration;

[Trait("Category", "Integration")]
public sealed class ManualsWebApplicationFactoryTests
{
    [Fact]
    public async Task StartingAgainstTheSharedRedisDatabase_IsRefusedBeforeAnythingIsWritten()
    {
        var sharedDatabase = RedisSettingKeys.SharedDatabase.ToString(CultureInfo.InvariantCulture);
        await using var configuredFactory = new ManualsWebApplicationFactory();
        await using var factory = configuredFactory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [RedisSettingKeys.Database] = sharedDatabase })));

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Equal(sharedDatabase, configuredFactory.RefusedDatabase);
    }
}
