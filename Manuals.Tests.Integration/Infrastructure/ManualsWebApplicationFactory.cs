namespace Manuals.Tests.Integration.Infrastructure;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using Manuals.Tests.Integration.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using StackExchange.Redis;

public sealed class ManualsWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    internal static readonly string TestScheme = Generated.LowercaseToken(12);

    internal static readonly string TestUserId = Generated.NewUserId().ToString();

    internal static readonly string TestEmailAddress = Generated.NewEmailAddress();

    private readonly HttpClient _openAIHttpClient;

    private bool _hostCreated;

    public ManualsWebApplicationFactory()
    {
        _openAIHttpClient = new HttpClient(OpenAIStub);
    }

    public string? RefusedDatabase { get; private set; }

    public OpenAIResponsesStub OpenAIStub { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await DeleteEveryKeyInTheTestDatabaseAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_hostCreated)
        {
            await DeleteEveryKeyInTheTestDatabaseAsync();
        }

        await base.DisposeAsync();
        _openAIHttpClient.Dispose();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [OpenAISettingKeys.Endpoint] = Generated.NewHttpsUri(Generated.NewHostname()).ToString(),
                [OpenAISettingKeys.ApiKey] = Generated.NewSecretValue(),
            }));
        var host = base.CreateHost(builder);
        _hostCreated = true;
        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices((ctx, services) =>
        {
            var database = ctx.Configuration[RedisSettingKeys.Database];
            if (!int.TryParse(database, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index != TestDatabaseContractConstants.TestDatabase)
            {
                RefusedDatabase = database;
                throw new InvalidOperationException(
                    $"The integration tier writes to the Redis database it is given, so it refuses '{database}': it must be the test database {TestDatabaseContractConstants.TestDatabase}.");
            }

            if (!ctx.HostingEnvironment.IsProduction())
            {
                services.RemoveAll<ILoggerFactory>();
                services.AddLogging(lb => lb.AddConsole());
            }

            services.RemoveAll<ResponsesClient>();
            services.AddSingleton(new ResponsesClient(
                new ApiKeyCredential(Generated.NewSecretValue()),
                new ResponsesClientOptions
                {
                    Transport = new HttpClientPipelineTransport(_openAIHttpClient),
                    RetryPolicy = new ClientRetryPolicy(0),
                }));

            services.AddAuthentication(TestScheme)
                .AddScheme<AuthenticationSchemeOptions, IntegrationAuthHandler>(TestScheme, _ => { });
        });
    }

    private async Task DeleteEveryKeyInTheTestDatabaseAsync()
    {
        var database = Services.GetRequiredService<IDatabase>();
        if (database.Database != TestDatabaseContractConstants.TestDatabase)
        {
            return;
        }

        foreach (var server in database.Multiplexer.GetServers())
        {
            await foreach (var key in server.KeysAsync(database.Database))
            {
                await database.KeyDeleteAsync(key);
            }
        }
    }
}
