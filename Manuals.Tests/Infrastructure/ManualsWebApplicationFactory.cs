namespace Manuals.Tests.Infrastructure;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> for integration tests.
/// Uses the real Redis and Azure OpenAI connections established by
/// <c>Manuals/Program.cs</c> at startup (requires <c>az login</c> / <c>azure/login</c>).
/// Replaces the JWT Bearer auth with a test scheme so tests can call the API
/// without a real access token, and replaces Serilog with a console logger.
/// </summary>
/// <remarks>
/// Tests using this factory must clean up any Redis keys they create.
/// Use the <c>integration@test.invalid</c> email prefix to identify test data:
/// Redis key <c>user:integration@test.invalid:chats</c>.
/// </remarks>
public sealed class ManualsWebApplicationFactory : WebApplicationFactory<Program>
{
    internal const string TestEmail = "integration@test.invalid";
    internal const string TestScheme = "Integration";

    /// <inheritdoc/>
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
