#pragma warning disable SA1200
using Manuals.Extensions;
using Manuals.Services;
using Microsoft.AspNetCore.HttpOverrides;
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
    builder.Services.AddScoped<IChatService, OpenAIChatService>();
    builder.Services.AddControllers();
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
    app.UseSerilogRequestLogging();
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
