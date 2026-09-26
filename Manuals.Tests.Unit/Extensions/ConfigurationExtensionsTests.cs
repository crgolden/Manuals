namespace Manuals.Tests.Unit.Extensions;

using Manuals.Extensions;
using Microsoft.Extensions.Configuration;

[Trait("Category", "Unit")]
public sealed class ConfigurationExtensionsTests
{
    [Fact]
    public void GetRequired_ThrowsRatherThanReturningZero_WhenAnIntKeyIsMissing()
    {
        // Arrange
        var missingKey = Generated.NewSettingKey();
        IConfiguration config = new ConfigurationBuilder().Build();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => config.GetRequired<int>(missingKey));

        // Assert
        Assert.Contains(missingKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetRequired_ThrowsRatherThanReturningFalse_WhenABoolKeyIsMissing()
    {
        // Arrange
        var missingKey = Generated.NewSettingKey();
        IConfiguration config = new ConfigurationBuilder().Build();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => config.GetRequired<bool>(missingKey));

        // Assert
        Assert.Contains(missingKey, exception.Message, StringComparison.Ordinal);
    }
}
