#pragma warning disable SA1200
#pragma warning disable OPENAI001
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Security.KeyVault.Secrets;
using Elastic.Ingest.Elasticsearch;
using Elastic.Ingest.Elasticsearch.DataStreams;
using Elastic.Serilog.Sinks;
using Elastic.Transport;
using Manuals.Extensions;
using Manuals.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Azure;
using OpenAI;
using OpenAI.Responses;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using StackExchange.Redis;
#pragma warning restore SA1200

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    Uri oidcAuthority = builder.Configuration.GetRequired<Uri>("OidcAuthority"),
        openAIEndpoint = builder.Configuration.GetRequired<Uri>("OpenAIEndpoint");
    var openAIClientOptions = new OpenAIClientOptions
    {
        Endpoint = new Uri($"{openAIEndpoint}openai/v1/")
    };
    ResponsesClient responsesClient;
    var redisHost = builder.Configuration.GetRequired<string>("RedisHost");
    var redisPort = builder.Configuration.GetRequired<int>("RedisPort");
    var redisSsl = builder.Configuration.GetRequired<bool>("RedisSsl");
    var redisEndpoint = new DnsEndPoint(redisHost, redisPort);
    var configurationOptions = new ConfigurationOptions
    {
        Ssl = redisSsl,
        EndPoints = [redisEndpoint]
    };
    if (builder.Environment.IsProduction())
    {
        var defaultAzureCredentialOptionsSection = builder.Configuration.GetRequiredSection(nameof(DefaultAzureCredentialOptions));
        var defaultAzureCredentialOptions = defaultAzureCredentialOptionsSection.Get<DefaultAzureCredentialOptions>() ?? throw new InvalidOperationException($"Invalid '{nameof(DefaultAzureCredentialOptions)}' section.");
        var tokenCredential = new DefaultAzureCredential(defaultAzureCredentialOptions);
        var bearerTokenPolicy = new BearerTokenPolicy(tokenCredential, "https://cognitiveservices.azure.com/.default");
        responsesClient = new ResponsesClient(bearerTokenPolicy, openAIClientOptions);
        Uri blobUri = builder.Configuration.GetRequired<Uri>("BlobUri"),
            dataProtectionKeyIdentifier = builder.Configuration.GetRequired<Uri>("DataProtectionKeyIdentifier"),
            elasticsearchNode = builder.Configuration.GetRequired<Uri>("ElasticsearchNode"),
            keyVaultUrl = builder.Configuration.GetRequired<Uri>("KeyVaultUri");
        var applicationName = builder.Configuration.GetRequired<string>("WEBSITE_SITE_NAME");
        var secretClient = new SecretClient(keyVaultUrl, tokenCredential);
        var secrets = secretClient.GetManualsSecrets();
        configurationOptions.Password = secrets.RedisPassword.Value;
        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
            options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase));
        builder.Logging.AddOpenTelemetry(openTelemetryLoggerOptions =>
        {
            openTelemetryLoggerOptions.IncludeFormattedMessage = true;
            openTelemetryLoggerOptions.IncludeScopes = true;
        });
        builder.Services
            .AddSerilog((serviceProvider, loggerConfiguration) => loggerConfiguration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(serviceProvider)
                .Enrich.WithProperty(nameof(IHostEnvironment.ApplicationName), applicationName)
                .WriteTo.Elasticsearch(
                    [elasticsearchNode],
                    elasticsearchSinkOptions =>
                    {
                        elasticsearchSinkOptions.DataStream = new DataStreamName("logs", "dotnet", nameof(Manuals));
                        elasticsearchSinkOptions.BootstrapMethod = BootstrapMethod.Failure;
                    },
                    transportConfiguration =>
                    {
                        var header = new BasicAuthentication(secrets.ElasticsearchUsername.Value, secrets.ElasticsearchPassword.Value);
                        transportConfiguration.Authentication(header);
                    }))
            .AddOpenTelemetry()
            .ConfigureResource(resourceBuilder => resourceBuilder
                .AddService(applicationName, null, typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0")
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = builder.Environment.EnvironmentName.ToLowerInvariant()
                }))
            .WithMetrics(meterProviderBuilder => meterProviderBuilder
                .AddRuntimeInstrumentation())
            .WithTracing(tracerProviderBuilder => tracerProviderBuilder
                .SetSampler(new AlwaysOnSampler())
                .AddSource(nameof(Manuals))
                .AddRedisInstrumentation())
            .UseAzureMonitor().Services
            .AddDataProtection()
            .SetApplicationName(applicationName)
            .PersistKeysToAzureBlobStorage(blobUri, tokenCredential)
            .ProtectKeysWithAzureKeyVault(dataProtectionKeyIdentifier, tokenCredential).Services
            .AddAzureClientsCore(true);
    }
    else
    {
        if (builder.Environment.IsDevelopment())
        {
            builder.Configuration.AddUserSecrets("4b268de6-5012-41fa-b8f6-254b6d08b380");
        }

        var secrets = builder.Configuration.GetManualsSecrets();
        configurationOptions.Password = secrets.RedisPassword;
        var apiKeyCredential = new ApiKeyCredential(secrets.OpenAIApiKey);
        responsesClient = new ResponsesClient(apiKeyCredential, openAIClientOptions);
        builder.Services
            .AddSerilog((serviceProvider, loggerConfiguration) => loggerConfiguration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(serviceProvider))
            .AddDataProtection()
            .UseEphemeralDataProtectionProvider();
    }

    var muxer = await ConnectionMultiplexer.ConnectAsync(configurationOptions);
    var database = muxer.GetDatabase();
    builder.Services.AddSingleton<IConnectionMultiplexer>(muxer);
    builder.Services.AddSingleton(database);
    builder.Services.AddSingleton(responsesClient);
    builder.Services.AddScoped<IChatsService, RedisChatsService>();
    builder.Services.AddControllers(options =>
    {
        options.SuppressAsyncSuffixInActionNames = false;
        var jsonFormatter = options.InputFormatters
            .OfType<SystemTextJsonInputFormatter>()
            .FirstOrDefault();
        jsonFormatter?.SupportedMediaTypes.Add("application/merge-patch+json");
    });
    builder.Services.AddOpenApi();
    builder.Services.AddHealthChecks();
    builder.Services
        .AddAuthentication()
        .AddJwtBearer(jwtBearerOptions =>
        {
            jwtBearerOptions.Authority = oidcAuthority.ToString();
            jwtBearerOptions.TokenValidationParameters.ValidateAudience = false;
            jwtBearerOptions.MapInboundClaims = false;
        }).Services
        .AddAuthorizationBuilder()
        .AddPolicy(nameof(Manuals), policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("scope", "manuals");
        });
    builder.Services.Configure<ForwardedHeadersOptions>(forwardedHeadersOptions =>
    {
        forwardedHeadersOptions.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        forwardedHeadersOptions.KnownIPNetworks.Clear();
        forwardedHeadersOptions.KnownProxies.Clear();
    });

    var app = builder.Build();
    app.UseForwardedHeaders();
    app.UseSerilogRequestLogging(options =>
    {
        options.EnrichDiagnosticContext = (diagnosticContext, _) =>
        {
            if (Activity.Current is null)
            {
                return;
            }

            diagnosticContext.Set(nameof(Activity.TraceId), Activity.Current.TraceId.ToString());
            diagnosticContext.Set(nameof(Activity.SpanId), Activity.Current.SpanId.ToString());
        };
    });
    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }
    else
    {
        app.UseHsts();
    }

    app.UseHttpsRedirection().UseAuthorization();
    app.Use((ctx, next) =>
    {
        if (ctx.User.Identity?.IsAuthenticated != true)
        {
            return next(ctx);
        }

        using (Serilog.Context.LogContext.PushProperty("UserId", ctx.User.FindFirstValue("sub")))
        using (Serilog.Context.LogContext.PushProperty("UserEmail", ctx.User.FindFirstValue("email")))
        {
            return next(ctx);
        }
    });
    app.MapOpenApi();
    app.MapHealthChecks("/health").DisableHttpMetrics();
    app.MapStaticAssets();
    app.MapControllers();
    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
#pragma warning restore OPENAI001
