namespace Manuals.Tests.Unit.Services;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Manuals.Services;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Models;
using Moq;
using OpenAI.Responses;
using StackExchange.Redis;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class RedisChatsServiceTests
{
    private const long UnparseableCreatedAt = 0L;

    private static readonly string TestEmail = TestValues.NewEmailAddress();
    private static readonly Guid TestChatId = Guid.NewGuid();
    private static readonly Guid NewestChatId = Guid.NewGuid();
    private static readonly Guid OldestChatId = Guid.NewGuid();
    private static readonly Guid UntitledChatId = Guid.NewGuid();
    private static readonly string ChatsKey = RedisChatsService.ChatsKey(TestEmail);
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Mock<IDatabase> _databaseMock = new(MockBehavior.Strict);
    private readonly IConfiguration _configuration;
    private readonly RedisChatsService _service;

    public RedisChatsServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RedisChatsService.ModelConfigurationKey] = TestValues.NewModelName(),
                [RedisChatsService.MaxOutputTokenCountConfigurationKey] = TestValues.NewMaxOutputTokenCount(),
                [RedisChatsService.InstructionsConfigurationKey] = TestValues.NewInstructions(),
            })
            .Build();

        _service = CreateService(Mock.Of<ResponsesClient>());
    }

    public enum OwnershipRequiredOperation
    {
        DeleteChat,
        GetChat,
        GetChatMessages,
        UpdateChatTitle,
    }

    public static TheoryData<OwnershipRequiredOperation> OwnershipRequiredOperations() => new()
    {
        OwnershipRequiredOperation.DeleteChat,
        OwnershipRequiredOperation.GetChat,
        OwnershipRequiredOperation.GetChatMessages,
        OwnershipRequiredOperation.UpdateChatTitle,
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
            () => _service.CompleteChatAsync(TestEmail, TestChatId, TestValues.NewBlank(), TestContext.Current.CancellationToken));
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
            () => _service.StreamChatAsync(TestEmail, TestChatId, TestValues.NewBlank(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(OwnershipRequiredOperations))]
    public async Task Operation_WhenChatNotOwnedByUser_ThrowsKeyNotFoundException(
        OwnershipRequiredOperation operation)
    {
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None))
            .ReturnsAsync((double?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => InvokeOwnershipRequiredAsync(operation, _service, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetChatsAsync_ReturnsMembersInDescendingOrder()
    {
        _databaseMock
            .Setup(d => d.SortedSetRangeByRankAsync(ChatsKey, 0, -1, Order.Descending, CommandFlags.None))
            .ReturnsAsync([RedisChatsService.ChatMember(NewestChatId), RedisChatsService.ChatMember(OldestChatId)]);

        var newestTitle = TestValues.NewChatTitle();
        var newestCreatedAt = TestValues.NewUnixSeconds();
        _databaseMock
            .Setup(d => d.HashGetAllAsync(RedisChatsService.ChatMetaKey(NewestChatId), CommandFlags.None))
            .ReturnsAsync([
                new HashEntry(RedisChatsService.TitleField, newestTitle),
                new HashEntry(RedisChatsService.CreatedAtField, newestCreatedAt)]);

        _databaseMock
            .Setup(d => d.HashGetAllAsync(RedisChatsService.ChatMetaKey(OldestChatId), CommandFlags.None))
            .ReturnsAsync([
                new HashEntry(RedisChatsService.TitleField, TestValues.NewChatTitle()),
                new HashEntry(RedisChatsService.CreatedAtField, TestValues.NewUnixSeconds())]);

        var result = await _service.GetChatsAsync(TestEmail, TestContext.Current.CancellationToken);

        Assert.Collection(
            result,
            newest =>
            {
                Assert.Equal(NewestChatId, newest.ChatId);
                Assert.Equal(newestTitle, newest.Title);
                Assert.Equal(newestCreatedAt, newest.CreatedAt);
            },
            oldest => Assert.Equal(OldestChatId, oldest.ChatId));
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
            .ReturnsAsync([RedisChatsService.ChatMember(UntitledChatId)]);

        _databaseMock
            .Setup(d => d.HashGetAllAsync(RedisChatsService.ChatMetaKey(UntitledChatId), CommandFlags.None))
            .ReturnsAsync([
                new HashEntry(RedisChatsService.TitleField, string.Empty),
                new HashEntry(RedisChatsService.CreatedAtField, TestValues.NewUnixSeconds())]);

        var result = await _service.GetChatsAsync(TestEmail, TestContext.Current.CancellationToken);

        var onlyChat = Assert.Single(result);
        Assert.Null(onlyChat.Title);
    }

    [Fact]
    public async Task GetChatAsync_WhenOwned_ReturnsChatWithMeta()
    {
        var score = TestValues.NewSortedSetScore();
        var title = TestValues.NewChatTitle();
        var createdAt = TestValues.NewUnixSeconds();
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None))
            .ReturnsAsync(score);
        _databaseMock
            .Setup(d => d.HashGetAllAsync(RedisChatsService.ChatMetaKey(TestChatId), CommandFlags.None))
            .ReturnsAsync([
                new HashEntry(RedisChatsService.TitleField, title),
                new HashEntry(RedisChatsService.CreatedAtField, createdAt)]);

        var result = await _service.GetChatAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken);

        Assert.Equal(TestChatId, result.ChatId);
        Assert.Equal(title, result.Title);
        Assert.Equal(createdAt, result.CreatedAt);
    }

    [Fact]
    public async Task GetChatMessagesAsync_WhenOwned_ReturnsDeserializedMessages()
    {
        var score = TestValues.NewSortedSetScore();
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None))
            .ReturnsAsync(score);

        var userText = TestValues.NewMessageText();
        var assistantText = TestValues.NewMessageText();
        var msg1 = JsonSerializer.Serialize(new ChatHistoryMessage(RedisChatsService.UserRole, userText), WebJsonOptions);
        var msg2 = JsonSerializer.Serialize(new ChatHistoryMessage(RedisChatsService.AssistantRole, assistantText), WebJsonOptions);
        _databaseMock
            .Setup(d => d.ListRangeAsync(RedisChatsService.ChatMessagesKey(TestChatId), 0, -1, CommandFlags.None))
            .ReturnsAsync([(RedisValue)msg1, (RedisValue)msg2]);

        var result = await _service.GetChatMessagesAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken);

        Assert.Collection(
            result,
            user =>
            {
                Assert.Equal(RedisChatsService.UserRole, user.Role);
                Assert.Equal(userText, user.Text);
            },
            assistant =>
            {
                Assert.Equal(RedisChatsService.AssistantRole, assistant.Role);
                Assert.Equal(assistantText, assistant.Text);
            });
    }

    [Fact]
    public async Task UpdateChatTitleAsync_WhenOwned_UpdatesHashField()
    {
        var score = TestValues.NewSortedSetScore();
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None))
            .ReturnsAsync(score);
        _databaseMock
            .Setup(d => d.HashSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        var renamedChatTitle = TestValues.NewChatTitle();

        await _service.UpdateChatTitleAsync(TestEmail, TestChatId, renamedChatTitle, TestContext.Current.CancellationToken);

        _databaseMock.Verify(
            d => d.HashSetAsync(
                It.Is<RedisKey>(k => string.Equals(k.ToString(), RedisChatsService.ChatMetaKey(TestChatId), StringComparison.Ordinal)),
                It.Is<RedisValue>(f => string.Equals(f.ToString(), RedisChatsService.TitleField, StringComparison.Ordinal)),
                It.Is<RedisValue>(v => string.Equals(v.ToString(), renamedChatTitle, StringComparison.Ordinal)),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteChatAsync_WhenOwned_RemovesChatAndKeys()
    {
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None))
            .ReturnsAsync(TestValues.NewSortedSetScore());
        _databaseMock
            .Setup(d => d.SortedSetRemoveAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None))
            .ReturnsAsync(true);
        RedisKey[] expectedDeletedKeys =
        [
            RedisChatsService.ChatMetaKey(TestChatId),
            RedisChatsService.ChatMessagesKey(TestChatId),
        ];
        _databaseMock
            .Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey[]>(), CommandFlags.None))
            .ReturnsAsync(expectedDeletedKeys.Length);

        await _service.DeleteChatAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken);

        _databaseMock.Verify(
            d => d.SortedSetRemoveAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None),
            Times.Once);
        _databaseMock.Verify(
            d => d.KeyDeleteAsync(
                It.Is<RedisKey[]>(keys => keys.SequenceEqual(expectedDeletedKeys)),
                CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task GetChatsAsync_SkipsNonGuidMembers()
    {
        _databaseMock
            .Setup(d => d.SortedSetRangeByRankAsync(ChatsKey, 0, -1, Order.Descending, CommandFlags.None))
            .ReturnsAsync([TestValues.NewNonGuidSortedSetMember(), RedisChatsService.ChatMember(NewestChatId)]);
        _databaseMock
            .Setup(d => d.HashGetAllAsync(RedisChatsService.ChatMetaKey(NewestChatId), CommandFlags.None))
            .ReturnsAsync([
                new HashEntry(RedisChatsService.TitleField, TestValues.NewChatTitle()),
                new HashEntry(RedisChatsService.CreatedAtField, TestValues.NewUnixSeconds())]);

        var result = await _service.GetChatsAsync(TestEmail, TestContext.Current.CancellationToken);

        var onlyChat = Assert.Single(result);
        Assert.Equal(NewestChatId, onlyChat.ChatId);
    }

    [Fact]
    public async Task CompleteChatAsync_WhenChatNotOwned_ThrowsKeyNotFoundException()
    {
        // Arrange
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None))
            .ReturnsAsync((double?)null);

        // Act
        var exception = await Record.ExceptionAsync(
            () => _service.CompleteChatAsync(TestEmail, TestChatId, TestValues.NewMessageText(), TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<KeyNotFoundException>(exception);
    }

    [Fact]
    public async Task GetChatAsync_WhenTitleEmptyAndCreatedAtUnparseable_ReturnsNullTitleAndZero()
    {
        // Arrange
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None))
            .ReturnsAsync(TestValues.NewSortedSetScore());
        _databaseMock
            .Setup(d => d.HashGetAllAsync(RedisChatsService.ChatMetaKey(TestChatId), CommandFlags.None))
            .ReturnsAsync([
                new HashEntry(RedisChatsService.TitleField, string.Empty),
                new HashEntry(RedisChatsService.CreatedAtField, TestValues.NewUnparseableTimestamp())]);

        // Act
        var result = await _service.GetChatAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result.Title);
        Assert.Equal(UnparseableCreatedAt, result.CreatedAt);
    }

    [Fact]
    public async Task GetChatMessagesAsync_WhenItemDeserializesToNull_SkipsItem()
    {
        // Arrange
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None))
            .ReturnsAsync(TestValues.NewSortedSetScore());
        var validText = TestValues.NewMessageText();
        var valid = JsonSerializer.Serialize(new ChatHistoryMessage(RedisChatsService.UserRole, validText), WebJsonOptions);
        var deserializesToNull = JsonSerializer.Serialize<ChatHistoryMessage?>(null, WebJsonOptions);
        _databaseMock
            .Setup(d => d.ListRangeAsync(RedisChatsService.ChatMessagesKey(TestChatId), 0, -1, CommandFlags.None))
            .ReturnsAsync([(RedisValue)valid, (RedisValue)deserializesToNull]);

        // Act
        var result = await _service.GetChatMessagesAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken);

        // Assert
        var onlyMessage = Assert.Single(result);
        Assert.Equal(validText, onlyMessage.Text);
    }

    [Fact]
    public void StreamChatAsync_WhenInputProvided_ReturnsEnumerator()
    {
        // Act
        var stream = _service.StreamChatAsync(TestEmail, TestChatId, TestValues.NewMessageText(), TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(stream);
    }

    [Fact]
    public async Task CompleteChatAsync_WithHistory_BuildsInputItemsBeforeCallingOpenAi()
    {
        // Arrange
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None))
            .ReturnsAsync(TestValues.NewSortedSetScore());
        var userMsg = JsonSerializer.Serialize(
            new ChatHistoryMessage(RedisChatsService.UserRole, TestValues.NewMessageText()), WebJsonOptions);
        var assistantMsg = JsonSerializer.Serialize(
            new ChatHistoryMessage(RedisChatsService.AssistantRole, TestValues.NewMessageText()), WebJsonOptions);
        _databaseMock
            .Setup(d => d.ListRangeAsync(RedisChatsService.ChatMessagesKey(TestChatId), 0, -1, CommandFlags.None))
            .ReturnsAsync([(RedisValue)userMsg, (RedisValue)assistantMsg]);
        var openAi = new Mock<ResponsesClient>(MockBehavior.Strict);
        openAi
            .Setup(c => c.CreateResponseAsync(It.IsAny<CreateResponseOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(TestValues.NewFailureReason()));
        var service = CreateService(openAi.Object);

        // Act
        var exception = await Record.ExceptionAsync(
            () => service.CompleteChatAsync(TestEmail, TestChatId, TestValues.NewMessageText(), TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        openAi.Verify(c => c.CreateResponseAsync(It.IsAny<CreateResponseOptions>(), It.IsAny<CancellationToken>()), Times.Once);
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
        _databaseMock.Verify(
            d => d.HashSetAsync(
                It.Is<RedisKey>(k => string.Equals(
                    k.ToString(), RedisChatsService.ChatMetaKey(chat.ChatId), StringComparison.Ordinal)),
                It.IsAny<HashEntry[]>(),
                CommandFlags.None),
            Times.Once);
        _databaseMock.Verify(d => d.SortedSetAddAsync(ChatsKey, It.IsAny<RedisValue>(), It.IsAny<double>(), It.IsAny<SortedSetWhen>(), CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task CompleteChatAsync_WhenOpenAiReturnsOutput_StoresMessagesSetsTitleAndReturnsText()
    {
        // Arrange
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None))
            .ReturnsAsync(TestValues.NewSortedSetScore());
        _databaseMock
            .Setup(d => d.ListRangeAsync(RedisChatsService.ChatMessagesKey(TestChatId), 0, -1, CommandFlags.None))
            .ReturnsAsync([]);
        _databaseMock
            .Setup(d => d.ListRightPushAsync(RedisChatsService.ChatMessagesKey(TestChatId), It.IsAny<RedisValue[]>(), It.IsAny<When>(), CommandFlags.None))
            .ReturnsAsync(TestValues.NewRedisListLength());
        _databaseMock
            .Setup(d => d.SortedSetAddAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), It.IsAny<double>(), It.IsAny<SortedSetWhen>(), CommandFlags.None))
            .ReturnsAsync(true);

        _databaseMock
            .Setup(d => d.HashGetAsync(RedisChatsService.ChatMetaKey(TestChatId), (RedisValue)RedisChatsService.TitleField, CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);
        _databaseMock
            .Setup(d => d.HashSetAsync(RedisChatsService.ChatMetaKey(TestChatId), (RedisValue)RedisChatsService.TitleField, It.IsAny<RedisValue>(), It.IsAny<When>(), CommandFlags.None))
            .ReturnsAsync(true);

        var input = TestValues.NewMessageText();
        var expectedOutput = TestValues.NewMessageText();
        var openAi = new Mock<ResponsesClient>(MockBehavior.Strict);
        openAi
            .Setup(c => c.CreateResponseAsync(It.IsAny<CreateResponseOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientResult.FromValue(BuildResponse(expectedOutput), Mock.Of<PipelineResponse>()));
        var service = CreateService(openAi.Object);

        // Act
        var (resultChatId, outputText) = await service.CompleteChatAsync(
            TestEmail, TestChatId, input, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(TestChatId, resultChatId);
        Assert.Equal(expectedOutput, outputText);
        _databaseMock.Verify(
            d => d.ListRightPushAsync(RedisChatsService.ChatMessagesKey(TestChatId), It.IsAny<RedisValue[]>(), It.IsAny<When>(), CommandFlags.None),
            Times.Once);
        _databaseMock.Verify(
            d => d.HashSetAsync(RedisChatsService.ChatMetaKey(TestChatId), (RedisValue)RedisChatsService.TitleField, (RedisValue)input, It.IsAny<When>(), CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task CompleteChatAsync_WhenOpenAiReturnsNoOutput_ThrowsInvalidOperationException()
    {
        // Arrange
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None))
            .ReturnsAsync(TestValues.NewSortedSetScore());
        _databaseMock
            .Setup(d => d.ListRangeAsync(RedisChatsService.ChatMessagesKey(TestChatId), 0, -1, CommandFlags.None))
            .ReturnsAsync([]);

        var openAi = new Mock<ResponsesClient>(MockBehavior.Strict);
        openAi
            .Setup(c => c.CreateResponseAsync(It.IsAny<CreateResponseOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientResult.FromValue(BuildEmptyResponse(), Mock.Of<PipelineResponse>()));
        var service = CreateService(openAi.Object);

        // Act
        var exception = await Record.ExceptionAsync(
            () => service.CompleteChatAsync(TestEmail, TestChatId, TestValues.NewMessageText(), TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task StreamChatAsync_WhenOpenAiStreamsDeltas_YieldsDeltasAndPersistsOnCompletion()
    {
        // Arrange
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None))
            .ReturnsAsync(TestValues.NewSortedSetScore());
        _databaseMock
            .Setup(d => d.ListRangeAsync(RedisChatsService.ChatMessagesKey(TestChatId), 0, -1, CommandFlags.None))
            .ReturnsAsync([]);
        _databaseMock
            .Setup(d => d.ListRightPushAsync(RedisChatsService.ChatMessagesKey(TestChatId), It.IsAny<RedisValue[]>(), It.IsAny<When>(), CommandFlags.None))
            .ReturnsAsync(TestValues.NewRedisListLength());
        _databaseMock
            .Setup(d => d.SortedSetAddAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), It.IsAny<double>(), It.IsAny<SortedSetWhen>(), CommandFlags.None))
            .ReturnsAsync(true);
        _databaseMock
            .Setup(d => d.HashGetAsync(RedisChatsService.ChatMetaKey(TestChatId), (RedisValue)RedisChatsService.TitleField, CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);
        _databaseMock
            .Setup(d => d.HashSetAsync(RedisChatsService.ChatMetaKey(TestChatId), (RedisValue)RedisChatsService.TitleField, It.IsAny<RedisValue>(), It.IsAny<When>(), CommandFlags.None))
            .ReturnsAsync(true);

        var input = TestValues.NewMessageText();
        var firstDelta = TestValues.NewMessageText();
        var secondDelta = TestValues.NewMessageText();
        var openAi = new Mock<ResponsesClient>(MockBehavior.Strict);
        openAi
            .Setup(c => c.CreateResponseStreamingAsync(It.IsAny<CreateResponseOptions>(), It.IsAny<CancellationToken>()))
            .Returns(new FakeStreamingResult(
                new StreamingResponseOutputTextDeltaUpdate { Delta = firstDelta },
                new StreamingResponseOutputTextDeltaUpdate { Delta = secondDelta }));
        var service = CreateService(openAi.Object);

        // Act
        var deltas = await DrainAsync(
            service.StreamChatAsync(TestEmail, TestChatId, input, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal([firstDelta, secondDelta], deltas);
        _databaseMock.Verify(
            d => d.ListRightPushAsync(RedisChatsService.ChatMessagesKey(TestChatId), It.IsAny<RedisValue[]>(), It.IsAny<When>(), CommandFlags.None),
            Times.Once);
        _databaseMock.Verify(
            d => d.HashSetAsync(RedisChatsService.ChatMetaKey(TestChatId), (RedisValue)RedisChatsService.TitleField, (RedisValue)input, It.IsAny<When>(), CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task StreamChatAsync_WhenStreamIsEmpty_PersistsNothing()
    {
        // Arrange
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, RedisChatsService.ChatMember(TestChatId), CommandFlags.None))
            .ReturnsAsync(TestValues.NewSortedSetScore());
        _databaseMock
            .Setup(d => d.ListRangeAsync(RedisChatsService.ChatMessagesKey(TestChatId), 0, -1, CommandFlags.None))
            .ReturnsAsync([]);

        var openAi = new Mock<ResponsesClient>(MockBehavior.Strict);
        openAi
            .Setup(c => c.CreateResponseStreamingAsync(It.IsAny<CreateResponseOptions>(), It.IsAny<CancellationToken>()))
            .Returns(new FakeStreamingResult());
        var service = CreateService(openAi.Object);

        // Act
        var deltas = await DrainAsync(
            service.StreamChatAsync(TestEmail, TestChatId, TestValues.NewMessageText(), TestContext.Current.CancellationToken));

        // Assert
        Assert.Empty(deltas);
        _databaseMock.Verify(
            d => d.ListRightPushAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue[]>(), It.IsAny<When>(), CommandFlags.None),
            Times.Never);
    }

    private static Task InvokeOwnershipRequiredAsync(
        OwnershipRequiredOperation operation,
        RedisChatsService service,
        CancellationToken cancellationToken) =>
        operation switch
        {
            OwnershipRequiredOperation.DeleteChat =>
                service.DeleteChatAsync(TestEmail, TestChatId, cancellationToken),
            OwnershipRequiredOperation.GetChat =>
                service.GetChatAsync(TestEmail, TestChatId, cancellationToken),
            OwnershipRequiredOperation.GetChatMessages =>
                service.GetChatMessagesAsync(TestEmail, TestChatId, cancellationToken),
            OwnershipRequiredOperation.UpdateChatTitle =>
                service.UpdateChatTitleAsync(TestEmail, TestChatId, TestValues.NewChatTitle(), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static async Task<IReadOnlyList<string>> DrainAsync(IAsyncEnumerable<string> source)
    {
        var drained = new List<string>();
        await foreach (var item in source)
        {
            drained.Add(item);
        }

        return drained;
    }

    private static ResponseResult BuildEmptyResponse() => new()
    {
        Id = TestValues.NewResponseId(),
        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(TestValues.NewUnixSeconds()),
        Status = ResponseStatus.Completed,
        Model = TestValues.NewModelName(),
        ParallelToolCallsEnabled = false,
    };

    private static ResponseResult BuildResponse(string outputText)
    {
        var response = BuildEmptyResponse();
        response.OutputItems.Add(ResponseItem.CreateAssistantMessageItem(outputText));
        return response;
    }

    private RedisChatsService CreateService(ResponsesClient responsesClient)
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var hybridCache = services.BuildServiceProvider().GetRequiredService<HybridCache>();
        return new RedisChatsService(responsesClient, _databaseMock.Object, hybridCache, _configuration);
    }

    private sealed class FakeStreamingResult(params StreamingResponseUpdate[] updates)
        : AsyncCollectionResult<StreamingResponseUpdate>
    {
        private readonly StreamingResponseUpdate[] _updates = updates;

        public override ContinuationToken? GetContinuationToken(ClientResult page) => null;

        public override async IAsyncEnumerable<ClientResult> GetRawPagesAsync()
        {
            await Task.Yield();
            yield return ClientResult.FromValue(new object(), Mock.Of<PipelineResponse>());
        }

        protected override async IAsyncEnumerable<StreamingResponseUpdate> GetValuesFromPageAsync(ClientResult page)
        {
            foreach (var update in _updates)
            {
                await Task.Yield();
                yield return update;
            }
        }
    }
}