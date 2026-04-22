namespace Manuals.Extensions;

using Azure.Core;

/// <summary>
/// A <see cref="TokenCredential"/> that caches the first token it acquires and returns it on subsequent
/// calls for the same scope until the token is within <see cref="RefreshBuffer"/> of expiry, at which
/// point it delegates to the underlying credential for a fresh token. Requests for a different scope
/// are always forwarded to the underlying credential uncached.
/// </summary>
/// <remarks>
/// This eliminates the redundant second <c>az account get-access-token</c> spawn that would otherwise occur
/// when <see cref="Azure.Core.BearerTokenAuthenticationPolicy"/> calls <c>GetTokenAsync</c> on the same
/// <see cref="Azure.Identity.DefaultAzureCredential"/> that was already pre-warmed by
/// <see cref="ConfigurationExtensions.ToTokenCredentialAsync"/>.
/// </remarks>
internal sealed class CachingTokenCredential : TokenCredential
{
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);

    private readonly TokenCredential _inner;

    private readonly TokenRequestContext _cachedContext;

    private AccessToken _cached;

    internal CachingTokenCredential(TokenCredential inner, TokenRequestContext cachedContext, AccessToken cached)
    {
        _inner = inner;
        _cachedContext = cachedContext;
        _cached = cached;
    }

    /// <inheritdoc/>
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        if (IsCacheValid(requestContext))
        {
            return _cached;
        }

        return _inner.GetToken(requestContext, cancellationToken);
    }

    /// <inheritdoc/>
    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        if (IsCacheValid(requestContext))
        {
            return _cached;
        }

        var token = await _inner.GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false);

        // Update the cache if this is the same scope we were originally created for.
        if (requestContext.Scopes.SequenceEqual(_cachedContext.Scopes))
        {
            _cached = token;
        }

        return token;
    }

    private bool IsCacheValid(TokenRequestContext requestContext) =>
        !IsExpiringSoon() && requestContext.Scopes.SequenceEqual(_cachedContext.Scopes);

    private bool IsExpiringSoon() => DateTimeOffset.UtcNow >= _cached.ExpiresOn - RefreshBuffer;
}
