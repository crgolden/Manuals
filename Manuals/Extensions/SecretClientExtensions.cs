namespace Manuals.Extensions;

using Azure.Security.KeyVault.Secrets;

public static class SecretClientExtensions
{
    extension(SecretClient secretClient)
    {
#pragma warning disable SA1009
        public (
            KeyVaultSecret ElasticsearchUsername,
            KeyVaultSecret ElasticsearchPassword,
            KeyVaultSecret RedisPassword
        ) GetManualsSecrets()
        {
            var elasticsearchUsername = secretClient.GetSecret("ElasticsearchUsername");
            var elasticsearchPassword = secretClient.GetSecret("ElasticsearchPassword");
            var redisPassword = secretClient.GetSecret("RedisPassword");
            return (
                elasticsearchUsername.Value,
                elasticsearchPassword.Value,
                redisPassword.Value
            );
        }
#pragma warning restore SA1009
    }
}