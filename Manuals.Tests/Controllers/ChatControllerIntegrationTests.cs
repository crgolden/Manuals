using Manuals.Models;
using Manuals.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Net;
using System.Net.Http.Json;

namespace Manuals.Tests.Controllers;

public sealed class ChatControllerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly Mock<IChatService> _mockChatService = new();
    private readonly Mock<IConversationHistoryStore> _mockHistoryStore = new();

    private HttpClient CreateClient(WebApplicationFactory<Program> factory)
        => factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AzureOpenAI:Endpoint"] = "https://fake.openai.azure.com",
                    ["AzureOpenAI:DeploymentName"] = "gpt-4o"
                }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(_mockChatService.Object);
                services.AddSingleton(_mockHistoryStore.Object);
            });
        }).CreateClient();

    [Fact]
    public async Task PostAsync_Returns200_WithValidRequest()
    {
        using var factory = new WebApplicationFactory<Program>();
        var expected = new ChatResponse("conv-1", "Paris", "stop");
        _mockChatService
            .Setup(s => s.CompleteChatAsync(It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var client = CreateClient(factory);
        var request = new ChatRequest("conv-1", "What is the capital of France?");

        var response = await client.PostAsJsonAsync("/api/chat", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.NotNull(result);
        Assert.Equal("Paris", result.Message);
        Assert.Equal("conv-1", result.ConversationId);
    }

    [Fact]
    public async Task PostAsync_Returns400_WhenMessageIsEmpty()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = CreateClient(factory);
        var request = new ChatRequest("conv-1", "");

        var response = await client.PostAsJsonAsync("/api/chat", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostAsync_Returns400_WhenConversationIdIsEmpty()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = CreateClient(factory);
        var request = new ChatRequest("", "Hello");

        var response = await client.PostAsJsonAsync("/api/chat", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Returns204_AndClearsHistory()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = CreateClient(factory);

        var response = await client.DeleteAsync("/api/chat/conv-1");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        _mockHistoryStore.Verify(s => s.Clear("conv-1"), Times.Once);
    }
}
