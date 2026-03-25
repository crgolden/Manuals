using Manuals.Hubs;
using Manuals.Options;
using Manuals.Services;

var builder = WebApplication.CreateBuilder(args);

// Options with startup validation — fails fast if AzureOpenAI:Endpoint is missing
builder.Services
    .AddOptions<AzureOpenAIOptions>()
    .BindConfiguration(AzureOpenAIOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Application services
builder.Services.AddSingleton<IConversationHistoryStore, InMemoryConversationHistoryStore>();
builder.Services.AddSingleton<IChatClientFactory, AzureOpenAIChatClientFactory>();
builder.Services.AddScoped<IChatService, AzureOpenAIChatService>();

// ASP.NET Core infrastructure
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthorization();

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

app.Run();

// Required for WebApplicationFactory<Program> in integration tests
public partial class Program { }
