namespace Manuals.Tests.Unit.Infrastructure;

[CollectionDefinition(IntegrationIdentityConstants.CollectionName)]
public sealed class IntegrationCollection : ICollectionFixture<ManualsWebApplicationFactory>
{
}
