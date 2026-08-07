namespace Manuals.Tests.Unit.Infrastructure;

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<ManualsWebApplicationFactory>
{
    public const string Name = "Integration";
}