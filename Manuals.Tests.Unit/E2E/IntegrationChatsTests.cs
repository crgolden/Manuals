namespace Manuals.Tests.Unit.E2E;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Infrastructure;
using Manuals.Services;
using Microsoft.Extensions.DependencyInjection;
using Models;
using StackExchange.Redis;

[Collection(IntegrationIdentityConstants.CollectionName)]
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
        // Arrange
        var chat = await CreateChatAsync();
        _createdChatIds.Add(chat.ChatId);

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

        // Act
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

        // Assert
        Assert.Contains("data:", body, StringComparison.Ordinal);
        Assert.Contains("[DONE]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversationHistoryIsPreserved()
    {
        // Arrange
        var chat = await CreateChatAsync();
        _createdChatIds.Add(chat.ChatId);

        var first = await _client.PostAsJsonAsync(
            $"/chats/{chat.ChatId}/messages",
            new ChatRequest("I need the manual for the Samsung QN90B TV."),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Act
        var second = await _client.PostAsJsonAsync(
            $"/chats/{chat.ChatId}/messages",
            new ChatRequest("What product did I just say I need a manual for? Reply with only the product name."),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var result = await second.Content.ReadFromJsonAsync<ChatResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result?.Output);
        Assert.Contains("QN90B", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var chatId in _createdChatIds)
        {
            await _database.KeyDeleteAsync(
                [RedisChatsService.ChatMetaKey(chatId), RedisChatsService.ChatMessagesKey(chatId)]);

            await _database.KeyDeleteAsync(L2Key(RedisChatsService.ChatMessagesCacheKey(chatId)));
        }

        await _database.SortedSetRemoveRangeByScoreAsync(
            RedisChatsService.ChatsKey(ManualsWebApplicationFactory.TestUserId),
            double.NegativeInfinity,
            double.PositiveInfinity);

        await _database.KeyDeleteAsync(
            L2Key(RedisChatsService.ChatListCacheKey(ManualsWebApplicationFactory.TestUserId)));
    }

    private static string L2Key(string cacheKey) => $"{RedisChatsService.CacheInstanceName}{cacheKey}";

    private async Task<Chat> CreateChatAsync()
    {
        var response = await _client.PostAsync(
            "/chats",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

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