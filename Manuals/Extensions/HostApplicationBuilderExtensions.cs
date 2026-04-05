#pragma warning disable OPENAI001
namespace Manuals.Extensions;

using System.ClientModel.Primitives;
using System.Net;
using Azure.Core;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Security.KeyVault.Secrets;
using Elastic.Ingest.Elasticsearch;
using Elastic.Ingest.Elasticsearch.DataStreams;
using Elastic.Serilog.Sinks;
using Elastic.Transport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Azure;
using OpenAI;
using OpenAI.Conversations;
using OpenAI.Responses;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using StackExchange.Redis;

public static class HostApplicationBuilderExtensions
{
    extension(IHostApplicationBuilder builder)
    {
        public async Task<IHostApplicationBuilder> AddObservabilityAsync(SecretClient secretClient, CancellationToken cancellationToken = default)
        {
            var elasticsearchNode = builder.Configuration.GetValue<Uri>("ElasticsearchNode") ?? throw new InvalidOperationException("Invalid 'ElasticsearchNode'.");
            var tasks = new[]
            {
                secretClient.GetSecretAsync("ElasticsearchUsername", cancellationToken: cancellationToken),
                secretClient.GetSecretAsync("ElasticsearchPassword", cancellationToken: cancellationToken),
            };
            var result = await Task.WhenAll(tasks);
            builder.Logging.AddOpenTelemetry(openTelemetryLoggerOptions =>
            {
                openTelemetryLoggerOptions.IncludeFormattedMessage = true;
                openTelemetryLoggerOptions.IncludeScopes = true;
            });
            builder.Services
                .AddOpenTelemetry()
                .ConfigureResource(x =>
                {
                    x.AddService(
                        serviceName: builder.Environment.ApplicationName,
                        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");
                    x.AddAttributes(new Dictionary<string, object>
                    {
                        ["deployment.environment"] = builder.Environment.EnvironmentName.ToLowerInvariant()
                    });
                })
                .UseAzureMonitor()
                .WithMetrics(meterProviderBuilder =>
                {
                    meterProviderBuilder
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation();
                })
                .WithTracing(tracerProviderBuilder =>
                {
                    tracerProviderBuilder
                        .SetSampler(new AlwaysOnSampler())
                        .AddSource(builder.Environment.ApplicationName)
                        .AddAspNetCoreInstrumentation(aspNetCoreTraceInstrumentationOptions =>
                        {
                            aspNetCoreTraceInstrumentationOptions.Filter = context =>
                            {
                                return !context.Request.Path.StartsWithSegments("/Health");
                            };
                        })
                        .AddHttpClientInstrumentation();
                    if (builder.Environment.IsDevelopment())
                    {
                        tracerProviderBuilder.AddConsoleExporter();
                    }
                }).Services
                .AddSerilog((sp, loggerConfiguration) =>
                {
                    loggerConfiguration
                        .ReadFrom.Configuration(builder.Configuration)
                        .ReadFrom.Services(sp)
                        .Enrich.FromLogContext();
                    if (builder.Environment.IsProduction())
                    {
                        loggerConfiguration
                            .WriteTo.Elasticsearch(
                                [elasticsearchNode],
                                elasticsearchSinkOptions =>
                                {
                                    elasticsearchSinkOptions.DataStream = new DataStreamName("logs", "dotnet", nameof(Manuals));
                                    elasticsearchSinkOptions.BootstrapMethod = BootstrapMethod.Failure;
                                },
                                transportConfiguration =>
                                {
                                    var header = new BasicAuthentication(result[0].Value.Value, result[1].Value.Value);
                                    transportConfiguration.Authentication(header);
                                });
                    }
                });
            return builder;
        }

        public IHostApplicationBuilder AddAuth()
        {
            var authority = builder.Configuration.GetValue<Uri?>("OidcAuthority") ?? throw new InvalidOperationException("Invalid 'OidcAuthority'.");
            builder.Services
                .AddAuthentication()
                .AddJwtBearer(jwtBearerOptions =>
                {
                    jwtBearerOptions.Authority = authority.ToString();
                    jwtBearerOptions.TokenValidationParameters.ValidateAudience = false;
                    jwtBearerOptions.MapInboundClaims = false;
                }).Services
                .AddAuthorizationBuilder()
                .AddPolicy(nameof(Manuals), policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.RequireClaim("scope", "manuals");
                });
            return builder;
        }

        public async Task<IHostApplicationBuilder> AddCachingAsync(SecretClient secretClient, CancellationToken cancellationToken = default)
        {
            var redisPassword = await secretClient.GetSecretAsync("RedisPassword", cancellationToken: cancellationToken) ?? throw new InvalidOperationException("Invalid 'Redis Password'.");
            var redisHost = builder.Configuration.GetValue<string?>("RedisHost") ?? throw new InvalidOperationException("Invalid 'RedisHost'.");
            var redisPort = builder.Configuration.GetValue<int?>("RedisPort") ?? throw new InvalidOperationException("Invalid 'RedisPort'.");
            var configurationOptions = new ConfigurationOptions
            {
                Ssl = true,
                Password = redisPassword.Value.Value,
                EndPoints = [new DnsEndPoint(redisHost, redisPort)]
            };
            var muxer = await ConnectionMultiplexer.ConnectAsync(configurationOptions);
            var database = muxer.GetDatabase();
            builder.Services.AddSingleton(database);
            return builder;
        }

        public IHostApplicationBuilder AddOpenAI(TokenCredential tokenCredential, string scope = "https://cognitiveservices.azure.com/.default")
        {
            var policy = new BearerTokenPolicy(tokenCredential, scope);
            var endpoint = builder.Configuration.GetValue<Uri?>("OpenAIEndpoint") ?? throw new InvalidOperationException("Invalid 'OpenAIEndpoint'.");
            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri($"{endpoint}openai/v1/")
            };
            var responsesClient = new ResponsesClient(policy, clientOptions);
            builder.Services.AddSingleton(responsesClient);
            var conversationClient = new ConversationClient(policy, clientOptions);
            builder.Services.AddSingleton(conversationClient);
            return builder;
        }

        public IHostApplicationBuilder AddDataProtection(TokenCredential tokenCredential)
        {
            var blobUrl = builder.Configuration.GetValue<Uri>("BlobUri") ?? throw new InvalidOperationException("Invalid 'BlobUri'.");
            var dataProtectionKeyIdentifier = builder.Configuration.GetValue<Uri>("DataProtectionKeyIdentifier") ?? throw new InvalidOperationException("Invalid 'DataProtectionKeyIdentifier'.");
            builder.Services
                .AddDataProtection()
                .SetApplicationName(builder.Environment.ApplicationName)
                .PersistKeysToAzureBlobStorage(blobUrl, tokenCredential)
                .ProtectKeysWithAzureKeyVault(dataProtectionKeyIdentifier, tokenCredential).Services
                .AddAzureClientsCore(true);
            return builder;
        }
    }
}
