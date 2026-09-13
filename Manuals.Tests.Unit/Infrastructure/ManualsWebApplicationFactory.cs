namespace Manuals.Tests.Unit.Infrastructure;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class ManualsWebApplicationFactory : WebApplicationFactory<Program>
{
    internal const string TestScheme = IntegrationIdentityConstants.TestScheme;

    internal const string TestUserId = IntegrationIdentityConstants.TestUserId;

    internal const string TestEmailAddress = IntegrationIdentityConstants.TestEmailAddress;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices((ctx, services) =>
        {
            if (!ctx.HostingEnvironment.IsProduction())
            {
                services.RemoveAll<ILoggerFactory>();
                services.AddLogging(lb => lb.AddConsole());
            }

            services.AddAuthentication(TestScheme)
                .AddScheme<AuthenticationSchemeOptions, IntegrationAuthHandler>(TestScheme, _ => { });

            services.AddAuthorizationBuilder()
                .AddPolicy(nameof(Manuals), policy =>
                    policy.RequireAuthenticatedUser().RequireClaim(
                        AuthorizationPolicies.ScopeClaimType, AuthorizationPolicies.ManualsScope));
        });
    }
}