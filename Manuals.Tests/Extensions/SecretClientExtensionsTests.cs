namespace Manuals.Tests.Extensions;

using Azure;
using Azure.Security.KeyVault.Secrets;
using Manuals.Extensions;
using Moq;

[Trait("Category", "Unit")]
public sealed class SecretClientExtensionsTests
{
    [Fact]
    public void GetManualsSecrets_ReturnsTupleWithAllThreeSecretValues()
    {
        var values = new Dictionary<string, string>
        {
            ["ElasticsearchUsername"] = "es-user",
            ["ElasticsearchPassword"] = "es-pass",
            ["RedisPassword"] = "redis-pass",
        };
        var mock = new Mock<SecretClient>();
        mock.Setup(c => c.GetSecret(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, SecretContentType?, CancellationToken>((name, _, _, _) => SecretResponse(name, values[name]));

        var (esUsername, esPassword, redisPassword) = mock.Object.GetManualsSecrets();

        Assert.Equal("es-user", esUsername.Value);
        Assert.Equal("es-pass", esPassword.Value);
        Assert.Equal("redis-pass", redisPassword.Value);
    }

    private static Response<KeyVaultSecret> SecretResponse(string name, string value) =>
        Response.FromValue(new KeyVaultSecret(name, value), Mock.Of<Response>());
}
