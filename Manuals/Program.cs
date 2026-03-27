#pragma warning disable SA1200
using System.Security.Claims;
using Azure.AI.OpenAI;
using Manuals.Extensions;
using Manuals.Hubs;
using Manuals.Services;
using OpenAI.Chat;
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
    var endpoint = builder.Configuration.GetValue<Uri>("AzureOpenAIEndpoint") ?? throw new InvalidOperationException("Invalid 'AzureOpenAIEndpoint'.");
    var deploymentName = builder.Configuration.GetValue<string>("AzureOpenAIDeploymentName") ?? throw new InvalidOperationException("Invalid 'AzureOpenAIDeploymentName'.");
    builder.Services.AddSingleton<ChatClient>(sp =>
    {
        var azureClient = new AzureOpenAIClient(endpoint, tokenCredential);
        return azureClient.GetChatClient(deploymentName);
    });
    builder.Services.AddSingleton<IConversationHistoryStore, InMemoryConversationHistoryStore>();
    builder.Services.AddScoped<IChatService, AzureOpenAIChatService>();
    builder.Services.AddControllers();
    builder.Services.AddSignalR();
    builder.Services.AddOpenApi();
    await builder.AddObservabilityAsync(secretClient);
    builder.AddDataProtection(tokenCredential);
    builder.Services.AddHealthChecks();
    await builder.AddAuthAsync(secretClient);

    var app = builder.Build();
    app.UseSerilogRequestLogging();
    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }
    else
    {
        app.UseHsts();
    }

    app.UseHttpsRedirection().UseAuthorization();
    app.MapHealthChecks("Health").DisableHttpMetrics();
    app.MapStaticAssets();
    app.MapControllers();
    app.MapHub<ChatHub>("/hubs/chat");
    app.MapGet("identity", (ClaimsPrincipal user) =>
        {
            return user.Claims.Select(c => new { c.Type, c.Value });
        })
        .RequireAuthorization("ApiScope");

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
