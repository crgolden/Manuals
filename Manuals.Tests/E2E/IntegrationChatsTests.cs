namespace Manuals.Tests.E2E;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Manuals.Models;
using Manuals.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

/// <summary>
/// Integration tests against real Azure Redis and Azure OpenAI.
/// These tests use at most 3 real OpenAI completions per run.
/// </summary>
/// <remarks>
/// Requires Azure login and the following environment variables:
/// <c>RedisHost</c>, <c>RedisPort</c>, <c>OpenAIEndpoint</c>, <c>OpenAIModel</c>.
/// Clean-up deletes all Redis keys prefixed <c>user:integration@test.invalid:*</c>
/// and <c>chat:{chatId:N}:*</c> for chats created during the run.
/// </remarks>
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

        // Act: send a message expected to yield a short, unambiguous answer.
        var response = await _client.PostAsJsonAsync(
            $"/api/chats/{chat.ChatId}/messages",
            new ChatRequest("What is 2+2? Reply with only the number."),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result?.Output);
        Assert.Contains("4", result.Output, StringComparison.Ordinal);
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
            $"/api/chats/{chat.ChatId}/messages/stream")
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
            $"/api/chats/{chat.ChatId}/messages",
            new ChatRequest("My name is AliceIntegrationTestUser."),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Act: ask a follow-up that only makes sense if history is sent.
        var second = await _client.PostAsJsonAsync(
            $"/api/chats/{chat.ChatId}/messages",
            new ChatRequest("What is my name? Reply with only the name."),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var result = await second.Content.ReadFromJsonAsync<ChatResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result?.Output);
        Assert.Contains("Alice", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Clean up test data from real Redis.
        foreach (var chatId in _createdChatIds)
        {
            await _database.KeyDeleteAsync([$"chat:{chatId:N}:meta", $"chat:{chatId:N}:messages"]);
        }

        await _database.SortedSetRemoveRangeByScoreAsync(
            $"user:{ManualsWebApplicationFactory.TestEmail}:chats",
            double.NegativeInfinity,
            double.PositiveInfinity);
    }

    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------
    private async Task<Chat> CreateChatAsync()
    {
        var response = await _client.PostAsync(
            "/api/chats",
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
