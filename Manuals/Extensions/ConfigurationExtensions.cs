namespace Manuals.Extensions;

using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

public static class ConfigurationExtensions
{
    extension(IConfiguration configuration)
    {
        public async Task<TokenCredential> ToTokenCredentialAsync(string scope = "https://vault.azure.net/.default", CancellationToken cancellationToken = default)
        {
            var options = configuration.GetSection(nameof(DefaultAzureCredentialOptions)).Get<DefaultAzureCredentialOptions>() ?? throw new InvalidOperationException($"Invalid '{nameof(DefaultAzureCredentialOptions)}' configuration.");
            var credential = new DefaultAzureCredential(options);
            var context = new TokenRequestContext([scope]);
            var token = await credential.GetTokenAsync(context, cancellationToken);
            if (IsNullOrWhiteSpace(token.Token))
            {
                throw new InvalidOperationException("Failed to acquire token for Azure Key Vault access.");
            }

            // Wrap in a caching credential so BearerTokenAuthenticationPolicy reuses this token
            // instead of spawning a second az process on the first GetSecretAsync call.
            return new CachingTokenCredential(credential, context, token);
        }

        public SecretClient ToSecretClient(TokenCredential credential)
        {
            var keyVaultUrl = configuration.GetValue<Uri>("KeyVaultUri") ?? throw new InvalidOperationException("Invalid 'KeyVaultUri'.");
            var secretClient = new SecretClient(keyVaultUrl, credential);
            return secretClient;
        }
    }
}
