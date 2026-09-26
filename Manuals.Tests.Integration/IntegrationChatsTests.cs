namespace Manuals.Tests.Integration;

using System.Net;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Manuals.Controllers;
using Manuals.Models;
using Manuals.Tests.Integration.Infrastructure;
using Manuals.Tests.Integration.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[Collection(IntegrationIdentityConstants.CollectionName)]
[Trait("Category", "Integration")]
public sealed class IntegrationChatsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly IntegrationPrompts _prompts;

    public IntegrationChatsTests(ManualsWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _prompts = IntegrationPrompts.From(factory.Services.GetRequiredService<IConfiguration>());
    }

    [Fact]
    public async Task RealOpenAICompletionResponds()
    {
        var chat = await CreateChatAsync();
        var anyUserMessage = Generated.NewDescription();

        var response = await _client.PostAsJsonAsync(
            $"/chats/{chat.ChatId}/messages",
            new ChatRequest(anyUserMessage),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result?.Output);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
    }

    [Fact]
    public async Task RealOpenAIStreamingResponds()
    {
        var chat = await CreateChatAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/chats/{chat.ChatId}/messages/stream");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new ChatRequest(_prompts.ManualRequest(Generated.NewProductModel()))),
            Encoding.UTF8,
            MediaTypeNames.Application.Json);
        using var streamResponse = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
        Assert.Equal(MediaTypeNames.Text.EventStream, streamResponse.Content.Headers.ContentType?.MediaType);

        var body = await streamResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains(ChatsController.SseDataPrefix, body, StringComparison.Ordinal);
        Assert.Contains(ChatsController.SseDoneToken, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConversationHistoryIsPreserved()
    {
        var chat = await CreateChatAsync();

        var productModel = Generated.NewProductModel();
        var first = await _client.PostAsJsonAsync(
            $"/chats/{chat.ChatId}/messages",
            new ChatRequest(_prompts.ManualRequest(productModel)),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await _client.PostAsJsonAsync(
            $"/chats/{chat.ChatId}/messages",
            new ChatRequest(_prompts.RecallProduct),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var result = await second.Content.ReadFromJsonAsync<ChatResponse>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result?.Output);
        Assert.Contains(productModel, result.Output, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _client.Dispose();

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
