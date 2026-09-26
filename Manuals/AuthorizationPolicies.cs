namespace Manuals;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

internal static class AuthorizationPolicies
{
    internal const string ScopeClaimType = OpenIdConnectParameterNames.Scope;

    internal const string ManualsScope = "manuals";

    internal const string SubjectClaimType = JwtRegisteredClaimNames.Sub;

    internal const string EmailClaimType = JwtRegisteredClaimNames.Email;
}
