namespace Manuals.Tests.Services;

using Manuals.Services;
using Microsoft.Extensions.Configuration;
using Models;
using Moq;
using StackExchange.Redis;

[Trait("Category", "Unit")]
public sealed class RedisChatsServiceTests
{
    private const string TestEmail = "test@example.com";
    private static readonly Guid TestChatId = new("aabbccdd-1122-3344-5566-778899aabbcc");
    private static readonly Guid NewestChatId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OldestChatId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid UntitledChatId = new("33333333-3333-3333-3333-333333333333");
    private static readonly string ChatsKey = $"user:{TestEmail}:chats";

    private readonly Mock<IDatabase> _databaseMock = new();
    private readonly RedisChatsService _service;

    public RedisChatsServiceTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAIModel"] = "gpt-4",
                ["OpenAIMaxOutputTokenCount"] = "1000",
            })
            .Build();

        _service = new RedisChatsService(null!, _databaseMock.Object, configuration);
    }

    [Fact]
    public async Task CompleteChatAsync_WhenInputIsEmpty_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.CompleteChatAsync(TestEmail, TestChatId, string.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompleteChatAsync_WhenInputIsWhitespace_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.CompleteChatAsync(TestEmail, TestChatId, "   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void StreamChatAsync_WhenInputIsEmpty_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => _service.StreamChatAsync(TestEmail, TestChatId, string.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void StreamChatAsync_WhenInputIsWhitespace_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => _service.StreamChatAsync(TestEmail, TestChatId, "   ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteChatAsync_WhenChatNotOwnedByUser_ThrowsKeyNotFoundException()
    {
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync((double?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.DeleteChatAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetChatAsync_WhenChatNotOwnedByUser_ThrowsKeyNotFoundException()
    {
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync((double?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.GetChatAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetChatMessagesAsync_WhenChatNotOwnedByUser_ThrowsKeyNotFoundException()
    {
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync((double?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.GetChatMessagesAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateChatTitleAsync_WhenChatNotOwnedByUser_ThrowsKeyNotFoundException()
    {
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync((double?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.UpdateChatTitleAsync(TestEmail, TestChatId, "New Title", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetChatsAsync_ReturnsMembersInDescendingOrder()
    {
        _databaseMock
            .Setup(d => d.SortedSetRangeByRankAsync(ChatsKey, 0, -1, Order.Descending, CommandFlags.None))
            .ReturnsAsync([NewestChatId.ToString("N"), OldestChatId.ToString("N")]);

        _databaseMock
            .Setup(d => d.HashGetAllAsync($"chat:{NewestChatId:N}:meta", CommandFlags.None))
            .ReturnsAsync([new HashEntry("title", "Newest Chat"), new HashEntry("createdAt", "1700000000")]);

        _databaseMock
            .Setup(d => d.HashGetAllAsync($"chat:{OldestChatId:N}:meta", CommandFlags.None))
            .ReturnsAsync([new HashEntry("title", "Oldest Chat"), new HashEntry("createdAt", "1699999000")]);

        var result = await _service.GetChatsAsync(TestEmail, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal(NewestChatId, result[0].ChatId);
        Assert.Equal("Newest Chat", result[0].Title);
        Assert.Equal(OldestChatId, result[1].ChatId);
        Assert.Equal(1_700_000_000L, result[0].CreatedAt);
    }

    [Fact]
    public async Task GetChatsAsync_WhenNoChats_ReturnsEmptyList()
    {
        _databaseMock
            .Setup(d => d.SortedSetRangeByRankAsync(ChatsKey, 0, -1, Order.Descending, CommandFlags.None))
            .ReturnsAsync([]);

        var result = await _service.GetChatsAsync(TestEmail, TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetChatsAsync_WhenTitleIsEmpty_ReturnsChatWithNullTitle()
    {
        _databaseMock
            .Setup(d => d.SortedSetRangeByRankAsync(ChatsKey, 0, -1, Order.Descending, CommandFlags.None))
            .ReturnsAsync([UntitledChatId.ToString("N")]);

        _databaseMock
            .Setup(d => d.HashGetAllAsync($"chat:{UntitledChatId:N}:meta", CommandFlags.None))
            .ReturnsAsync([new HashEntry("title", string.Empty), new HashEntry("createdAt", "1700000000")]);

        var result = await _service.GetChatsAsync(TestEmail, TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Null(result[0].Title);
    }

    [Fact]
    public async Task CreateChatAsync_AddsToChatsSortedSetAndHashMeta()
    {
        _databaseMock
            .Setup(d => d.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), CommandFlags.None))
            .Returns(Task.CompletedTask);
        _databaseMock
            .Setup(d => d.SortedSetAddAsync(ChatsKey, It.IsAny<RedisValue>(), It.IsAny<double>(), It.IsAny<SortedSetWhen>(), CommandFlags.None))
            .ReturnsAsync(true);

        var chat = await _service.CreateChatAsync(TestEmail, TestContext.Current.CancellationToken);

        Assert.NotEqual(Guid.Empty, chat.ChatId);
        Assert.Null(chat.Title);
        Assert.True(chat.CreatedAt > 0);
        _databaseMock.Verify(d => d.HashSetAsync(It.Is<RedisKey>(k => k.ToString().StartsWith("chat:") && k.ToString().EndsWith(":meta")), It.IsAny<HashEntry[]>(), CommandFlags.None), Times.Once);
        _databaseMock.Verify(d => d.SortedSetAddAsync(ChatsKey, It.IsAny<RedisValue>(), It.IsAny<double>(), It.IsAny<SortedSetWhen>(), CommandFlags.None), Times.Once);
    }
}
