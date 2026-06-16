namespace Manuals.Tests.Services;

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
    public async Task CompleteChatAsync_WhenChatNotOwned_ThrowsKeyNotFoundException()
    {
        // Arrange — non-blank input passes the guard, then VerifyOwnership throws before any OpenAI call
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync((double?)null);

        // Act / Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.CompleteChatAsync(TestEmail, TestChatId, "hello", TestContext.Current.CancellationToken));
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
            .ReturnsAsync([new HashEntry("title", string.Empty), new HashEntry("createdAt", "not-a-number")]);

        // Act
        var result = await _service.GetChatAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result.Title);
        Assert.Equal(0L, result.CreatedAt);
    }

    [Fact]
    public async Task GetChatMessagesAsync_WhenItemDeserializesToNull_SkipsItem()
    {
        // Arrange — a literal "null" JSON element deserializes to null and must be skipped
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(1.0);
        var valid = JsonSerializer.Serialize(new ChatHistoryMessage("user", "Hello"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _databaseMock
            .Setup(d => d.ListRangeAsync($"chat:{TestChatId:N}:messages", 0, -1, CommandFlags.None))
            .ReturnsAsync([(RedisValue)valid, (RedisValue)"null"]);

        // Act
        var result = await _service.GetChatMessagesAsync(TestEmail, TestChatId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(result);
        Assert.Equal("Hello", result[0].Text);
    }

    [Fact]
    public void StreamChatAsync_WhenInputProvided_ReturnsEnumerator()
    {
        // Act — a non-blank input returns the lazy streaming enumerator without invoking OpenAI
        var stream = _service.StreamChatAsync(TestEmail, TestChatId, "hello", TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(stream);
    }

    [Fact]
    public async Task CompleteChatAsync_WithHistory_BuildsInputItemsBeforeCallingOpenAi()
    {
        // Arrange — a user+assistant history exercises BuildInputItems' role branches; the OpenAI call then
        // faults, which is enough to confirm the input items were assembled and the call was reached
        _databaseMock
            .Setup(d => d.SortedSetScoreAsync(ChatsKey, TestChatId.ToString("N"), CommandFlags.None))
            .ReturnsAsync(1.0);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var userMsg = JsonSerializer.Serialize(new ChatHistoryMessage("user", "Question"), jsonOptions);
        var assistantMsg = JsonSerializer.Serialize(new ChatHistoryMessage("assistant", "Answer"), jsonOptions);
        _databaseMock
            .Setup(d => d.ListRangeAsync($"chat:{TestChatId:N}:messages", 0, -1, CommandFlags.None))
            .ReturnsAsync([(RedisValue)userMsg, (RedisValue)assistantMsg]);
        var openAi = new Mock<ResponsesClient>(MockBehavior.Strict);
        openAi
            .Setup(c => c.CreateResponseAsync(It.IsAny<CreateResponseOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("OpenAI unavailable"));
        var service = CreateService(openAi.Object);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteChatAsync(TestEmail, TestChatId, "follow-up", TestContext.Current.CancellationToken));
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
        _databaseMock.Verify(d => d.HashSetAsync(It.Is<RedisKey>(k => k.ToString().StartsWith("chat:") && k.ToString().EndsWith(":meta")), It.IsAny<HashEntry[]>(), CommandFlags.None), Times.Once);
        _databaseMock.Verify(d => d.SortedSetAddAsync(ChatsKey, It.IsAny<RedisValue>(), It.IsAny<double>(), It.IsAny<SortedSetWhen>(), CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task CompleteChatAsync_WhenOpenAiReturnsOutput_StoresMessagesSetsTitleAndReturnsText()
    {
        // Arrange — no prior history, OpenAI returns a real ResponseResult built from wire JSON
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

        // Auto-title path: existing title is blank, so SetAutoTitleIfNeededAsync writes one
        _databaseMock
            .Setup(d => d.HashGetAsync($"chat:{TestChatId:N}:meta", (RedisValue)"title", CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);
        _databaseMock
            .Setup(d => d.HashSetAsync($"chat:{TestChatId:N}:meta", (RedisValue)"title", It.IsAny<RedisValue>(), It.IsAny<When>(), CommandFlags.None))
            .ReturnsAsync(true);

        var openAi = new Mock<ResponsesClient>(MockBehavior.Strict);
        openAi
            .Setup(c => c.CreateResponseAsync(It.IsAny<CreateResponseOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClientResult.FromValue(BuildResponse("Found the manual."), Mock.Of<PipelineResponse>()));
        var service = CreateService(openAi.Object);

        // Act
        var (resultChatId, outputText) = await service.CompleteChatAsync(
            TestEmail, TestChatId, "where is the manual?", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(TestChatId, resultChatId);
        Assert.Equal("Found the manual.", outputText);
        _databaseMock.Verify(
            d => d.ListRightPushAsync($"chat:{TestChatId:N}:messages", It.IsAny<RedisValue[]>(), It.IsAny<When>(), CommandFlags.None),
            Times.Once);
        _databaseMock.Verify(
            d => d.HashSetAsync($"chat:{TestChatId:N}:meta", (RedisValue)"title", (RedisValue)"where is the manual?", It.IsAny<When>(), CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task CompleteChatAsync_WhenOpenAiReturnsNoOutput_ThrowsInvalidOperationException()
    {
        // Arrange — owned chat, no history, OpenAI returns a response with no message output (GetOutputText null)
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

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteChatAsync(TestEmail, TestChatId, "where is the manual?", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StreamChatAsync_WhenOpenAiStreamsDeltas_YieldsDeltasAndPersistsOnCompletion()
    {
        // Arrange — owned chat, no history; the streamed deltas accumulate to "Hello world"
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

        var openAi = new Mock<ResponsesClient>(MockBehavior.Strict);
        openAi
            .Setup(c => c.CreateResponseStreamingAsync(It.IsAny<CreateResponseOptions>(), It.IsAny<CancellationToken>()))
            .Returns(new FakeStreamingResult(
                new StreamingResponseOutputTextDeltaUpdate { Delta = "Hello " },
                new StreamingResponseOutputTextDeltaUpdate { Delta = "world" }));
        var service = CreateService(openAi.Object);

        // Act
        var deltas = new List<string>();
        await foreach (var delta in service.StreamChatAsync(TestEmail, TestChatId, "hi", TestContext.Current.CancellationToken))
        {
            deltas.Add(delta);
        }

        // Assert
        Assert.Equal(["Hello ", "world"], deltas);
        _databaseMock.Verify(
            d => d.ListRightPushAsync($"chat:{TestChatId:N}:messages", It.IsAny<RedisValue[]>(), It.IsAny<When>(), CommandFlags.None),
            Times.Once);
        _databaseMock.Verify(
            d => d.HashSetAsync($"chat:{TestChatId:N}:meta", (RedisValue)"title", (RedisValue)"hi", It.IsAny<When>(), CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task StreamChatAsync_WhenStreamIsEmpty_PersistsNothing()
    {
        // Arrange — owned chat, no history, and a stream that yields no deltas
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
        var deltas = new List<string>();
        await foreach (var delta in service.StreamChatAsync(TestEmail, TestChatId, "hi", TestContext.Current.CancellationToken))
        {
            deltas.Add(delta);
        }

        // Assert — accumulated text is empty, so no persistence occurs
        Assert.Empty(deltas);
        _databaseMock.Verify(
            d => d.ListRightPushAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue[]>(), It.IsAny<When>(), CommandFlags.None),
            Times.Never);
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

    // Minimal AsyncCollectionResult that yields a fixed sequence of streaming updates, mirroring the
    // AsyncSseUpdateCollection the real client returns (ResponsesClient.cs line 266) without a live SSE stream.
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
