namespace Manuals.Tests.Services;

using Manuals.Services;
using Microsoft.Extensions.Configuration;
using Moq;
using StackExchange.Redis;

[Trait("Category", "Unit")]
public sealed class OpenAIChatServiceTests
{
    private const string TestEmail = "test@example.com";
    private const string TestConversationId = "conv-abc";
    private const string RedisKey = $"user:{TestEmail}:conversations";

    private readonly Mock<IDatabase> _databaseMock = new();
    private readonly OpenAIChatService _service;

    public OpenAIChatServiceTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAIModel"] = "gpt-4",
                ["OpenAIMaxOutputTokenCount"] = "1000",
            })
            .Build();

        _service = new OpenAIChatService(null!, null!, _databaseMock.Object, configuration);
    }

    [Fact]
    public async Task CompleteChatAsync_WhenInputIsEmpty_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.CompleteChatAsync(TestEmail, string.Empty, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompleteChatAsync_WhenInputIsWhitespace_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.CompleteChatAsync(TestEmail, "   ", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void StreamChatAsync_WhenInputIsEmpty_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => _service.StreamChatAsync(TestEmail, string.Empty, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void StreamChatAsync_WhenInputIsWhitespace_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => _service.StreamChatAsync(TestEmail, "   ", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteConversationAsync_WhenConversationIdIsEmpty_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.DeleteConversationAsync(TestEmail, string.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteConversationAsync_WhenConversationIdIsWhitespace_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.DeleteConversationAsync(TestEmail, "   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteConversationAsync_WhenConversationNotOwnedByUser_ThrowsKeyNotFoundException()
    {
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(RedisKey, TestConversationId, CommandFlags.None))
            .ReturnsAsync((double?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.DeleteConversationAsync(TestEmail, TestConversationId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetConversationAsync_WhenConversationNotOwnedByUser_ThrowsKeyNotFoundException()
    {
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(RedisKey, TestConversationId, CommandFlags.None))
            .ReturnsAsync((double?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.GetConversationAsync(TestEmail, TestConversationId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetConversationItemsAsync_WhenConversationNotOwnedByUser_ThrowsKeyNotFoundException()
    {
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(RedisKey, TestConversationId, CommandFlags.None))
            .ReturnsAsync((double?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.GetConversationItemsAsync(TestEmail, TestConversationId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetConversationsAsync_ReturnsMembersInDescendingOrder()
    {
        _databaseMock
            .Setup(d => d.SortedSetRangeByRankAsync(RedisKey, 0, -1, Order.Descending, CommandFlags.None))
            .ReturnsAsync(["conv-newest", "conv-middle", "conv-oldest"]);

        var result = await _service.GetConversationsAsync(TestEmail, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Count);
        Assert.Equal("conv-newest", result[0]);
        Assert.Equal("conv-oldest", result[2]);
    }

    [Fact]
    public async Task GetConversationsAsync_WhenNoConversations_ReturnsEmptyList()
    {
        _databaseMock
            .Setup(d => d.SortedSetRangeByRankAsync(RedisKey, 0, -1, Order.Descending, CommandFlags.None))
            .ReturnsAsync([]);

        var result = await _service.GetConversationsAsync(TestEmail, TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }
}
