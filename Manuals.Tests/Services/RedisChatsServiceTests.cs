namespace Manuals.Tests.Services;

using System.Text.Json;
using Manuals.Services;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    private readonly Mock<IDatabase> _databaseMock = new(MockBehavior.Strict);
    private readonly RedisChatsService _service;

    public RedisChatsServiceTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAIModel"] = "gpt-4",
                ["OpenAIMaxOutputTokenCount"] = "1000",
                ["OpenAIInstructions"] = "test instructions",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddHybridCache();
        var provider = services.BuildServiceProvider();
        var hybridCache = provider.GetRequiredService<HybridCache>();

        _service = new RedisChatsService(null!, _databaseMock.Object, hybridCache, configuration);
    }

    public static TheoryData<Func<RedisChatsService, CancellationToken, Task>> OwnershipRequiredOperations() => new()
    {
        (s, ct) => s.DeleteChatAsync(TestEmail, TestChatId, ct),
        (s, ct) => s.GetChatAsync(TestEmail, TestChatId, ct),
        (s, ct) => s.GetChatMessagesAsync(TestEmail, TestChatId, ct),
        (s, ct) => s.UpdateChatTitleAsync(TestEmail, TestChatId, "New Title", ct),
    };

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

    [Theory]
    [MemberData(nameof(OwnershipRequiredOperations))]
    public async Task Operation_WhenChatNotOwnedByUser_ThrowsKeyNotFoundException(
        Func<RedisChatsService, CancellationToken, Task> operation)
    {
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync((double?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => operation(_service, TestContext.Current.CancellationToken));
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
    public async Task GetChatAsync_WhenOwned_ReturnsChatWithMeta()
    {
        const double score = 1_700_000_000_000.0;
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(score);
        _databaseMock
            .Setup(d => d.HashGetAllAsync($"chat:{TestChatId:N}:meta", CommandFlags.None))
            .ReturnsAsync([new HashEntry("title", "My Chat"), new HashEntry("createdAt", "1700000000")]);

        var result = await _service.GetChatAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken);

        Assert.Equal(TestChatId, result.ChatId);
        Assert.Equal("My Chat", result.Title);
        Assert.Equal(1_700_000_000L, result.CreatedAt);
    }

    [Fact]
    public async Task GetChatMessagesAsync_WhenOwned_ReturnsDeserializedMessages()
    {
        const double score = 1.0;
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(score);

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var msg1 = JsonSerializer.Serialize(new ChatHistoryMessage("user", "Hello"), jsonOptions);
        var msg2 = JsonSerializer.Serialize(new ChatHistoryMessage("assistant", "Hi there"), jsonOptions);
        _databaseMock
            .Setup(d => d.ListRangeAsync($"chat:{TestChatId:N}:messages", 0, -1, CommandFlags.None))
            .ReturnsAsync([(RedisValue)msg1, (RedisValue)msg2]);

        var result = await _service.GetChatMessagesAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("user", result[0].Role);
        Assert.Equal("Hello", result[0].Text);
        Assert.Equal("assistant", result[1].Role);
        Assert.Equal("Hi there", result[1].Text);
    }

    [Fact]
    public async Task UpdateChatTitleAsync_WhenOwned_UpdatesHashField()
    {
        const double score = 1.0;
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(score);
        _databaseMock
            .Setup(d => d.HashSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        await _service.UpdateChatTitleAsync(TestEmail, TestChatId, "New Title", TestContext.Current.CancellationToken);

        _databaseMock.Verify(
            d => d.HashSetAsync(
                It.Is<RedisKey>(k => k.ToString() == $"chat:{TestChatId:N}:meta"),
                It.Is<RedisValue>(f => f.ToString() == "title"),
                It.Is<RedisValue>(v => v.ToString() == "New Title"),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteChatAsync_WhenOwned_RemovesChatAndKeys()
    {
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(1.0);
        _databaseMock
            .Setup(d => d.SortedSetRemoveAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(true);
        _databaseMock
            .Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey[]>(), CommandFlags.None))
            .ReturnsAsync(2L);

        await _service.DeleteChatAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken);

        _databaseMock.Verify(
            d => d.SortedSetRemoveAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None),
            Times.Once);
        _databaseMock.Verify(
            d => d.KeyDeleteAsync(
                It.Is<RedisKey[]>(keys =>
                    keys.Length == 2 &&
                    keys[0].ToString() == $"chat:{TestChatId:N}:meta" &&
                    keys[1].ToString() == $"chat:{TestChatId:N}:messages"),
                CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task GetChatsAsync_SkipsNonGuidMembers()
    {
        _databaseMock
            .Setup(d => d.SortedSetRangeByRankAsync(ChatsKey, 0, -1, Order.Descending, CommandFlags.None))
            .ReturnsAsync(["not-a-guid", NewestChatId.ToString("N")]);
        _databaseMock
            .Setup(d => d.HashGetAllAsync($"chat:{NewestChatId:N}:meta", CommandFlags.None))
            .ReturnsAsync([new HashEntry("title", "Valid Chat"), new HashEntry("createdAt", "1700000000")]);

        var result = await _service.GetChatsAsync(TestEmail, TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(NewestChatId, result[0].ChatId);
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
