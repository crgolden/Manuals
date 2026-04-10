namespace Manuals.Tests.Infrastructure;

/// <summary>
/// xUnit collection that shares <see cref="ManualsWebApplicationFactory"/> across all
/// integration tests. One factory instance is created per test run, which avoids
/// repeated Azure Key Vault calls and Redis connection setup.
/// </summary>
[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<ManualsWebApplicationFactory>
{
    public const string Name = "Integration";
}
