namespace Manuals.Tests.Infrastructure;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Test-only authentication handler for nightly E2E tests.
/// Always authenticates as a nightly test user with the <c>manuals</c> scope.
/// </summary>
internal sealed class NightlyAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public NightlyAuthHandler(
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
            new Claim("sub", "nightly-user-id"),
            new Claim("email", "nightly@test.invalid"),
            new Claim("scope", "manuals"),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
