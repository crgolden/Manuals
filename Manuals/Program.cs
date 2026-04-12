#pragma warning disable SA1200
using System.Diagnostics;
using Manuals.Extensions;
using Manuals.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Formatters;
using Serilog;
#pragma warning restore SA1200

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    if (builder.Environment.IsDevelopment())
    {
        builder.Configuration.AddUserSecrets("4b268de6-5012-41fa-b8f6-254b6d08b380");
    }

    var tokenCredential = await builder.Configuration.ToTokenCredentialAsync();
    var secretClient = builder.Configuration.ToSecretClient(tokenCredential);
    await builder.AddCachingAsync(secretClient);
    builder.AddOpenAI(tokenCredential);
    builder.Services.AddScoped<IChatsService, RedisChatsService>();
    builder.Services.AddControllers(options =>
    {
        // Keep "Async" in action names so nameof(GetChatAsync) matches the registered
        // action name and CreatedAtAction can generate the Location header correctly.
        // The framework default (SuppressAsyncSuffixInActionNames = true) would strip
        // the suffix and cause a 500 from CreatedAtActionResult.OnFormatting.
        options.SuppressAsyncSuffixInActionNames = false;

        var jsonFormatter = options.InputFormatters
            .OfType<SystemTextJsonInputFormatter>()
            .FirstOrDefault();
        jsonFormatter?.SupportedMediaTypes.Add("application/merge-patch+json");
    });
    builder.Services.AddSignalR();
    builder.Services.AddOpenApi();
    await builder.AddObservabilityAsync(secretClient);
    builder.AddDataProtection(tokenCredential);
    builder.Services.AddHealthChecks();
    builder.AddAuth();
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
            var activity = Activity.Current;
            if (activity is null)
            {
                return;
            }

            diagnosticContext.Set("TraceId", activity.TraceId.ToString());
            diagnosticContext.Set("SpanId", activity.SpanId.ToString());
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

    app.MapOpenApi();
    app.UseHttpsRedirection().UseAuthorization();
    app.MapHealthChecks("Health").DisableHttpMetrics();
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

public partial class Program
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Program"/> class.
    /// Required for <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>.
    /// </summary>
    protected Program()
    {
    }
}
