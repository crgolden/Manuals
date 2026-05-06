namespace Manuals.Extensions;

public static class ConfigurationExtensions
{
    extension(IConfiguration configuration)
    {
        public T GetRequired<T>(string key)
            where T : notnull
        {
            return configuration.GetValue<T?>(key) ?? throw new InvalidOperationException($"Invalid '{key}'.");
        }

#pragma warning disable SA1009
        internal (
            string RedisPassword,
            string OpenAIApiKey
        ) GetManualsSecrets()
        {
            var redisPassword = configuration.GetRequired<string>("RedisPassword");
            var openAIApiKey = configuration.GetRequired<string>("OpenAIApiKey");
            return (
                redisPassword,
                openAIApiKey
            );
        }
#pragma warning restore SA1009
    }
}
