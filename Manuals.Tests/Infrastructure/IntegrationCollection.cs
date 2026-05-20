namespace Manuals.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<ManualsWebApplicationFactory>
{
    public const string Name = "Integration";
}
