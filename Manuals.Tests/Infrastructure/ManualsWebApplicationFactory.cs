namespace Manuals.Tests.Infrastructure;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class ManualsWebApplicationFactory : WebApplicationFactory<Program>
{
    internal const string TestUserId = "integration-user-id";

    internal const string TestScheme = "Integration";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices((ctx, services) =>
        {
            if (!ctx.HostingEnvironment.IsEnvironment("Production"))
            {
                // Replace Serilog (Elasticsearch sink) with a plain console logger.
                services.RemoveAll<ILoggerFactory>();
                services.AddLogging(lb => lb.AddConsole());
            }

            // Replace JWT Bearer auth with a test scheme that always succeeds.
            services.AddAuthentication(TestScheme)
                .AddScheme<AuthenticationSchemeOptions, IntegrationAuthHandler>(TestScheme, _ => { });

            // Replace the Manuals authorization policy so it accepts the test scheme.
            services.AddAuthorizationBuilder()
                .AddPolicy(nameof(Manuals), policy =>
                    policy.RequireAuthenticatedUser().RequireClaim("scope", "manuals"));
        });
    }
}
