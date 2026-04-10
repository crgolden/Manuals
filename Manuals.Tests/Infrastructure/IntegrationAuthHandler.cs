namespace Manuals.Tests.Infrastructure;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Test-only authentication handler for integration tests.
/// Always authenticates as an integration test user with the <c>manuals</c> scope.
/// </summary>
internal sealed class IntegrationAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public IntegrationAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim("sub", "integration-user-id"),
            new Claim("email", "integration@test.invalid"),
            new Claim("scope", "manuals"),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
