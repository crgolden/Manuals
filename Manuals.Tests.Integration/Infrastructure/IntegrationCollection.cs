namespace Manuals.Tests.Integration.Infrastructure;

[CollectionDefinition(IntegrationIdentityConstants.CollectionName)]
public sealed class IntegrationCollection : ICollectionFixture<ManualsWebApplicationFactory>
{
}
