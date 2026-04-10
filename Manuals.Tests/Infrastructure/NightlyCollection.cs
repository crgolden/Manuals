namespace Manuals.Tests.Infrastructure;

/// <summary>
/// xUnit collection that shares <see cref="ManualsWebApplicationFactory"/> across all
/// nightly E2E tests. One factory instance is created per test run, which avoids
/// repeated Azure Key Vault calls and Redis connection setup.
/// </summary>
[CollectionDefinition(Name)]
public sealed class NightlyCollection : ICollectionFixture<ManualsWebApplicationFactory>
{
    public const string Name = "Nightly";
}
