namespace Manuals.Tests.Extensions;

using Manuals.Extensions;
using Microsoft.Extensions.Configuration;

[Trait("Category", "Unit")]
public sealed class ConfigurationExtensionsTests
{
    [Fact]
    public void GetRequired_ReturnsValue_WhenKeyExists()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Foo"] = "bar" })
            .Build();

        Assert.Equal("bar", config.GetRequired<string>("Foo"));
    }

    [Fact]
    public void GetRequired_ThrowsInvalidOperationExceptionWithKeyName_WhenKeyMissing()
    {
        IConfiguration config = new ConfigurationBuilder().Build();

        var ex = Assert.Throws<InvalidOperationException>(() => config.GetRequired<string>("Missing"));
        Assert.Equal("Invalid 'Missing'.", ex.Message);
    }

    [Fact]
    public void GetManualsSecrets_ReadsBothConfiguredKeys()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RedisPassword"] = "redis-pass",
                ["OpenAIApiKey"] = "openai-key",
            })
            .Build();

        var (redisPassword, openAIApiKey) = config.GetManualsSecrets();

        Assert.Equal("redis-pass", redisPassword);
        Assert.Equal("openai-key", openAIApiKey);
    }
}
