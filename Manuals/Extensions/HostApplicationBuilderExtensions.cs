namespace Manuals.Extensions;

using Azure.Core;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Security.KeyVault.Secrets;
using Elastic.Ingest.Elasticsearch;
using Elastic.Ingest.Elasticsearch.DataStreams;
using Elastic.Serilog.Sinks;
using Elastic.Transport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Azure;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

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
                .ConfigureResource(x => x.AddService(builder.Environment.ApplicationName))
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
                    if (!builder.Environment.IsProduction())
                    {
                        return;
                    }

                    loggerConfiguration
                        .WriteTo.OpenTelemetry()
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
                });
            return builder;
        }

        public async Task<IHostApplicationBuilder> AddAuthAsync(SecretClient secretClient, CancellationToken cancellationToken = default)
        {
            var authority = await secretClient.GetSecretAsync("OidcAuthority", cancellationToken: cancellationToken);
            builder.Services
                .AddAuthentication()
                .AddJwtBearer(jwtBearerOptions =>
                {
                    jwtBearerOptions.Authority = authority.Value.Value;
                    jwtBearerOptions.TokenValidationParameters.ValidateAudience = false;
                }).Services
                .AddAuthorizationBuilder()
                .AddPolicy("ApiScope", policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.RequireClaim("krupamjo-api-1", "api1");
                });
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
