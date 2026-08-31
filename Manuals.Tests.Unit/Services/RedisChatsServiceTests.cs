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
    private static readonly string TestEmail = TestValues.NewEmailAddress();
    private static readonly Guid TestChatId = Guid.NewGuid();
    private static readonly Guid NewestChatId = Guid.NewGuid();
    private static readonly Guid OldestChatId = Guid.NewGuid();
    private static readonly Guid UntitledChatId = Guid.NewGuid();
    private static readonly string ChatsKey = $"user:{TestEmail}:chats";
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Mock<IDatabase> _databaseMock = new(MockBehavior.Strict);
    private readonly IConfiguration _configuration;
    private readonly RedisChatsService _service;

    public RedisChatsServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAIModel"] = "gpt-4",
                ["OpenAIMaxOutputTokenCount"] = "1000",
                ["OpenAIInstructions"] = "test instructions",
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
        OwnershipRequiredOperation operation)
    {
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync((double?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => InvokeOwnershipRequiredAsync(operation, _service, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetChatsAsync_ReturnsMembersInDescendingOrder()
    {
        _databaseMock
            .Setup(d => d.SortedSetRangeByRankAsync(ChatsKey, 0, -1, Order.Descending, CommandFlags.None))
            .ReturnsAsync([NewestChatId.ToString("N"), OldestChatId.ToString("N")]);

        var newestTitle = TestValues.NewChatTitle();
        var newestCreatedAt = TestValues.NewUnixSeconds();
        _databaseMock
            .Setup(d => d.HashGetAllAsync($"chat:{NewestChatId:N}:meta", CommandFlags.None))
            .ReturnsAsync([
                new HashEntry("title", newestTitle),
                new HashEntry("createdAt", newestCreatedAt)]);

        _databaseMock
            .Setup(d => d.HashGetAllAsync($"chat:{OldestChatId:N}:meta", CommandFlags.None))
            .ReturnsAsync([
                new HashEntry("title", TestValues.NewChatTitle()),
                new HashEntry("createdAt", TestValues.NewUnixSeconds())]);

        var result = await _service.GetChatsAsync(TestEmail, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal(NewestChatId, result[0].ChatId);
        Assert.Equal(newestTitle, result[0].Title);
        Assert.Equal(OldestChatId, result[1].ChatId);
        Assert.Equal(newestCreatedAt, result[0].CreatedAt);
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
            .ReturnsAsync([
                new HashEntry("title", string.Empty),
                new HashEntry("createdAt", TestValues.NewUnixSeconds())]);

        var result = await _service.GetChatsAsync(TestEmail, TestContext.Current.CancellationToken);

        var onlyChat = Assert.Single(result);
        Assert.Null(onlyChat.Title);
    }

    [Fact]
    public async Task GetChatAsync_WhenOwned_ReturnsChatWithMeta()
    {
        const double score = 1.0;
        var title = TestValues.NewChatTitle();
        var createdAt = TestValues.NewUnixSeconds();
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(score);
        _databaseMock
            .Setup(d => d.HashGetAllAsync($"chat:{TestChatId:N}:meta", CommandFlags.None))
            .ReturnsAsync([
                new HashEntry("title", title),
                new HashEntry("createdAt", createdAt)]);

        var result = await _service.GetChatAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken);

        Assert.Equal(TestChatId, result.ChatId);
        Assert.Equal(title, result.Title);
        Assert.Equal(createdAt, result.CreatedAt);
    }

    [Fact]
    public async Task GetChatMessagesAsync_WhenOwned_ReturnsDeserializedMessages()
    {
        const double score = 1.0;
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(score);

        var userText = TestValues.NewMessageText();
        var assistantText = TestValues.NewMessageText();
        var msg1 = JsonSerializer.Serialize(new ChatHistoryMessage("user", userText), WebJsonOptions);
        var msg2 = JsonSerializer.Serialize(new ChatHistoryMessage("assistant", assistantText), WebJsonOptions);
        _databaseMock
            .Setup(d => d.ListRangeAsync($"chat:{TestChatId:N}:messages", 0, -1, CommandFlags.None))
            .ReturnsAsync([(RedisValue)msg1, (RedisValue)msg2]);

        var result = await _service.GetChatMessagesAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("user", result[0].Role);
        Assert.Equal(userText, result[0].Text);
        Assert.Equal("assistant", result[1].Role);
        Assert.Equal(assistantText, result[1].Text);
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

        var renamedChatTitle = TestValues.NewChatTitle();

        await _service.UpdateChatTitleAsync(TestEmail, TestChatId, renamedChatTitle, TestContext.Current.CancellationToken);

        _databaseMock.Verify(
            d => d.HashSetAsync(
                It.Is<RedisKey>(k => string.Equals(k.ToString(), $"chat:{TestChatId:N}:meta", StringComparison.Ordinal)),
                It.Is<RedisValue>(f => string.Equals(f.ToString(), "title", StringComparison.Ordinal)),
                It.Is<RedisValue>(v => string.Equals(v.ToString(), renamedChatTitle, StringComparison.Ordinal)),
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
            .ReturnsAsync([TestValues.LowercaseToken(12), NewestChatId.ToString("N")]);
        _databaseMock
            .Setup(d => d.HashGetAllAsync($"chat:{NewestChatId:N}:meta", CommandFlags.None))
            .ReturnsAsync([
                new HashEntry("title", TestValues.NewChatTitle()),
                new HashEntry("createdAt", TestValues.NewUnixSeconds())]);

        var result = await _service.GetChatsAsync(TestEmail, TestContext.Current.CancellationToken);

        var onlyChat = Assert.Single(result);
        Assert.Equal(NewestChatId, onlyChat.ChatId);
    }

    [Fact]
    public async Task CompleteChatAsync_WhenChatNotOwned_ThrowsKeyNotFoundException()
    {
        // Arrange
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
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
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(1.0);
        _databaseMock
            .Setup(d => d.HashGetAllAsync($"chat:{TestChatId:N}:meta", CommandFlags.None))
            .ReturnsAsync([
                new HashEntry("title", string.Empty),
                new HashEntry("createdAt", TestValues.LowercaseToken(9))]);

        // Act
        var result = await _service.GetChatAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result.Title);
        Assert.Equal(0L, result.CreatedAt);
    }

    [Fact]
    public async Task GetChatMessagesAsync_WhenItemDeserializesToNull_SkipsItem()
    {
        // Arrange
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(1.0);
        var validText = TestValues.NewMessageText();
        var valid = JsonSerializer.Serialize(new ChatHistoryMessage("user", validText), WebJsonOptions);
        _databaseMock
            .Setup(d => d.ListRangeAsync($"chat:{TestChatId:N}:messages", 0, -1, CommandFlags.None))
            .ReturnsAsync([(RedisValue)valid, (RedisValue)"null"]);

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
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(1.0);
        var userMsg = JsonSerializer.Serialize(
            new ChatHistoryMessage("user", TestValues.NewMessageText()), WebJsonOptions);
        var assistantMsg = JsonSerializer.Serialize(
            new ChatHistoryMessage("assistant", TestValues.NewMessageText()), WebJsonOptions);
        _databaseMock
            .Setup(d => d.ListRangeAsync($"chat:{TestChatId:N}:messages", 0, -1, CommandFlags.None))
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
                It.Is<RedisKey>(k =>
                    k.ToString().StartsWith("chat:", StringComparison.Ordinal) &&
                    k.ToString().EndsWith(":meta", StringComparison.Ordinal)),
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
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(1.0);
        _databaseMock
            .Setup(d => d.ListRangeAsync($"chat:{TestChatId:N}:messages", 0, -1, CommandFlags.None))
            .ReturnsAsync([]);
        _databaseMock
            .Setup(d => d.ListRightPushAsync($"chat:{TestChatId:N}:messages", It.IsAny<RedisValue[]>(), It.IsAny<When>(), CommandFlags.None))
            .ReturnsAsync(2L);
        _databaseMock
            .Setup(d => d.SortedSetAddAsync(ChatsKey, TestChatId.ToString("N"), It.IsAny<double>(), It.IsAny<SortedSetWhen>(), CommandFlags.None))
            .ReturnsAsync(true);

        _databaseMock
            .Setup(d => d.HashGetAsync($"chat:{TestChatId:N}:meta", (RedisValue)"title", CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);
        _databaseMock
            .Setup(d => d.HashSetAsync($"chat:{TestChatId:N}:meta", (RedisValue)"title", It.IsAny<RedisValue>(), It.IsAny<When>(), CommandFlags.None))
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
            d => d.ListRightPushAsync($"chat:{TestChatId:N}:messages", It.IsAny<RedisValue[]>(), It.IsAny<When>(), CommandFlags.None),
            Times.Once);
        _databaseMock.Verify(
            d => d.HashSetAsync($"chat:{TestChatId:N}:meta", (RedisValue)"title", (RedisValue)input, It.IsAny<When>(), CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task CompleteChatAsync_WhenOpenAiReturnsNoOutput_ThrowsInvalidOperationException()
    {
        // Arrange
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(1.0);
        _databaseMock
            .Setup(d => d.ListRangeAsync($"chat:{TestChatId:N}:messages", 0, -1, CommandFlags.None))
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
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(1.0);
        _databaseMock
            .Setup(d => d.ListRangeAsync($"chat:{TestChatId:N}:messages", 0, -1, CommandFlags.None))
            .ReturnsAsync([]);
        _databaseMock
            .Setup(d => d.ListRightPushAsync($"chat:{TestChatId:N}:messages", It.IsAny<RedisValue[]>(), It.IsAny<When>(), CommandFlags.None))
            .ReturnsAsync(2L);
        _databaseMock
            .Setup(d => d.SortedSetAddAsync(ChatsKey, TestChatId.ToString("N"), It.IsAny<double>(), It.IsAny<SortedSetWhen>(), CommandFlags.None))
            .ReturnsAsync(true);
        _databaseMock
            .Setup(d => d.HashGetAsync($"chat:{TestChatId:N}:meta", (RedisValue)"title", CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);
        _databaseMock
            .Setup(d => d.HashSetAsync($"chat:{TestChatId:N}:meta", (RedisValue)"title", It.IsAny<RedisValue>(), It.IsAny<When>(), CommandFlags.None))
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
            d => d.ListRightPushAsync($"chat:{TestChatId:N}:messages", It.IsAny<RedisValue[]>(), It.IsAny<When>(), CommandFlags.None),
            Times.Once);
        _databaseMock.Verify(
            d => d.HashSetAsync($"chat:{TestChatId:N}:meta", (RedisValue)"title", (RedisValue)input, It.IsAny<When>(), CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task StreamChatAsync_WhenStreamIsEmpty_PersistsNothing()
    {
        // Arrange
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(1.0);
        _databaseMock
            .Setup(d => d.ListRangeAsync($"chat:{TestChatId:N}:messages", 0, -1, CommandFlags.None))
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

    private static ResponseResult BuildEmptyResponse()
    {
        const string json = """
        {
          "id": "resp_test",
          "object": "response",
          "created_at": 1700000000,
          "status": "completed",
          "model": "gpt-4",
          "parallel_tool_calls": false,
          "output": []
        }
        """;
        return ModelReaderWriter.Read<ResponseResult>(BinaryData.FromString(json))
            ?? throw new InvalidOperationException("Failed to build ResponseResult.");
    }

    private static ResponseResult BuildResponse(string outputText)
    {
        var json = $$"""
        {
          "id": "resp_test",
          "object": "response",
          "created_at": 1700000000,
          "status": "completed",
          "model": "gpt-4",
          "parallel_tool_calls": false,
          "output": [
            {
              "type": "message",
              "id": "msg_1",
              "status": "completed",
              "role": "assistant",
              "content": [ { "type": "output_text", "text": {{JsonSerializer.Serialize(outputText)}}, "annotations": [] } ]
            }
          ]
        }
        """;
        return ModelReaderWriter.Read<ResponseResult>(BinaryData.FromString(json))
            ?? throw new InvalidOperationException("Failed to build ResponseResult.");
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