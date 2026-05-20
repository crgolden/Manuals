namespace Manuals.Tests.E2E;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Manuals.Models;
using Manuals.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class IntegrationChatsTests : IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly IDatabase _database;
    private readonly List<Guid> _createdChatIds = [];

    public IntegrationChatsTests(ManualsWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _database = factory.Services.GetRequiredService<IDatabase>();
    }

    [Fact]
    public async Task RealOpenAICompletionResponds()
    {
        // Arrange: create a chat.
        var chat = await CreateChatAsync();
        _createdChatIds.Add(chat.ChatId);

        // Act: send a domain-appropriate question so the system prompt does not reject it.
        // This test verifies the end-to-end completion pipeline (HTTP → OpenAI → Redis → HTTP),
        // not the specific wording of the model's answer.
        var response = await _client.PostAsJsonAsync(
            $"/chats/{chat.ChatId}/messages",
            new ChatRequest("Can you help me find the manual for an LG OLED TV?"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result?.Output);
        Assert.False(string.IsNullOrWhiteSpace(result.Output), "Expected a non-empty response from the completion endpoint.");
    }

    [Fact]
    public async Task RealOpenAIStreamingResponds()
    {
        // Arrange
        var chat = await CreateChatAsync();
        _createdChatIds.Add(chat.ChatId);

        // Act: stream a message.
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/chats/{chat.ChatId}/messages/stream")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new ChatRequest("Say exactly: hello")),
                Encoding.UTF8,
                "application/json"),
        };
        using var streamResponse = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
        Assert.Equal("text/event-stream", streamResponse.Content.Headers.ContentType?.MediaType);

        var body = await streamResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert: the streamed body contains at least one data line.
        Assert.Contains("data:", body, StringComparison.Ordinal);
        Assert.Contains("[DONE]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversationHistoryIsPreserved()
    {
        // Arrange: create a chat and send a first message that establishes context.
        var chat = await CreateChatAsync();
        _createdChatIds.Add(chat.ChatId);

        var first = await _client.PostAsJsonAsync(
            $"/chats/{chat.ChatId}/messages",
            new ChatRequest("My name is AliceIntegrationTestUser."),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Act: ask a follow-up that only makes sense if history is sent.
        var second = await _client.PostAsJsonAsync(
            $"/chats/{chat.ChatId}/messages",
            new ChatRequest("What is my name? Reply with only the name."),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var result = await second.Content.ReadFromJsonAsync<ChatResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result?.Output);
        Assert.Contains("Alice", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        // Clean up test data from real Redis.
        foreach (var chatId in _createdChatIds)
        {
            await _database.KeyDeleteAsync([$"chat:{chatId:N}:meta", $"chat:{chatId:N}:messages"]);
        }

        await _database.SortedSetRemoveRangeByScoreAsync(
            $"user:{ManualsWebApplicationFactory.TestUserId}:chats",
            double.NegativeInfinity,
            double.PositiveInfinity);
    }

    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------
    private async Task<Chat> CreateChatAsync()
    {
        var response = await _client.PostAsync(
            "/chats",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Verify the Location header is present and resolves to the GET endpoint.
        // This is the only test tier that exercises CreatedAtActionResult.OnFormatting
        // through the real HTTP pipeline; the controller unit tests call the action
        // method directly and never trigger route URL generation.
        var location = response.Headers.Location;
        Assert.NotNull(location);

        var chat = await response.Content.ReadFromJsonAsync<Chat>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(chat);
        Assert.Contains(
            chat.ChatId.ToString("D"),
            location.ToString(),
            StringComparison.OrdinalIgnoreCase);

        return chat;
    }
}
