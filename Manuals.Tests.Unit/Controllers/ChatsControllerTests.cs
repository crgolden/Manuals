namespace Manuals.Tests.Unit.Controllers;

using System.Runtime.CompilerServices;
using System.Security.Claims;
using Manuals.Controllers;
using Manuals.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Models;
using Moq;
using TestSupport;

[Trait("Category", "Unit")]
public sealed class ChatsControllerTests
{
    private static readonly string TestUserId = TestValues.NewUserId();
    private static readonly Guid TestChatId = Guid.NewGuid();
    private static readonly Guid MissingChatId = Guid.Empty;

    private readonly Mock<IChatsService> _chatsServiceMock = new();
    private readonly ChatsController _controller;

    public ChatsControllerTests()
    {
        _controller = new ChatsController(_chatsServiceMock.Object);
    }

    [Fact]
    public async Task GetChatsAsync_ReturnsOkWithList()
    {
        _controller.ControllerContext = CreateContextWithUser();
        var firstTitle = TestValues.NewChatTitle();
        IReadOnlyList<Chat> chats =
        [
            new Chat(Guid.NewGuid(), firstTitle, TestValues.NewUnixSeconds()),
            new Chat(Guid.NewGuid(), TestValues.NewChatTitle(), TestValues.NewUnixSeconds()),
        ];
        _chatsServiceMock
            .Setup(s => s.GetChatsAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(chats);

        var result = await _controller.GetChatsAsync(TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsType<IReadOnlyList<Chat>>(ok.Value, exactMatch: false);
        Assert.Equal(2, list.Count);
        Assert.Equal(firstTitle, list[0].Title);
    }

    [Fact]
    public async Task GetChatsAsync_WhenEmpty_ReturnsOkWithEmptyList()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatsServiceMock
            .Setup(s => s.GetChatsAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await _controller.GetChatsAsync(TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsType<IReadOnlyList<Chat>>(ok.Value, exactMatch: false);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetChatAsync_ReturnsOkWithChat()
    {
        _controller.ControllerContext = CreateContextWithUser();
        var title = TestValues.NewChatTitle();
        var createdAt = TestValues.NewUnixSeconds();
        var chat = new Chat(TestChatId, title, createdAt);
        _chatsServiceMock
            .Setup(s => s.GetChatAsync(TestUserId, TestChatId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(chat);

        var result = await _controller.GetChatAsync(TestChatId, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsType<Chat>(ok.Value);
        Assert.Equal(TestChatId, returned.ChatId);
        Assert.Equal(title, returned.Title);
        Assert.Equal(createdAt, returned.CreatedAt);
    }

    [Fact]
    public async Task GetChatAsync_WhenNotFound_ReturnsNotFound()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatsServiceMock
            .Setup(s => s.GetChatAsync(TestUserId, MissingChatId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.GetChatAsync(MissingChatId, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetChatMessagesAsync_ReturnsOkWithMessages()
    {
        _controller.ControllerContext = CreateContextWithUser();
        var assistantText = TestValues.NewMessageText();
        IReadOnlyList<ChatHistoryMessage> messages =
        [
            new ChatHistoryMessage("user", TestValues.NewMessageText()),
            new ChatHistoryMessage("assistant", assistantText),
        ];
        _chatsServiceMock
            .Setup(s => s.GetChatMessagesAsync(TestUserId, TestChatId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(messages);

        var result = await _controller.GetChatMessagesAsync(TestChatId, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsType<IReadOnlyList<ChatHistoryMessage>>(ok.Value, exactMatch: false);
        Assert.Equal(2, returned.Count);
        Assert.Equal("user", returned[0].Role);
        Assert.Equal(assistantText, returned[1].Text);
    }

    [Fact]
    public async Task GetChatMessagesAsync_WhenNotFound_ReturnsNotFound()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatsServiceMock
            .Setup(s => s.GetChatMessagesAsync(TestUserId, MissingChatId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.GetChatMessagesAsync(MissingChatId, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task PostChatAsync_ReturnsCreatedAtActionWithChat()
    {
        _controller.ControllerContext = CreateContextWithUser();
        var chat = new Chat(TestChatId, null, TestValues.NewUnixSeconds());
        _chatsServiceMock
            .Setup(s => s.CreateChatAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(chat);

        var result = await _controller.PostChatAsync(TestContext.Current.CancellationToken);

        var created = Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(nameof(ChatsController.GetChatAsync), created.ActionName);
        Assert.Equal(TestChatId, created.RouteValues?["chatId"]);
        var returned = Assert.IsType<Chat>(created.Value);
        Assert.Equal(TestChatId, returned.ChatId);
    }

    [Fact]
    public async Task PatchChatAsync_WhenTitleIsNull_ReturnsBadRequest()
    {
        _controller.ControllerContext = CreateContextWithUser();
        var patch = new ChatPatchRequest(null);

        var result = await _controller.PatchChatAsync(TestChatId, patch, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PatchChatAsync_WhenTitleIsWhitespace_ReturnsBadRequest()
    {
        _controller.ControllerContext = CreateContextWithUser();
        var patch = new ChatPatchRequest("   ");

        var result = await _controller.PatchChatAsync(TestChatId, patch, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PatchChatAsync_WhenValid_ReturnsNoContent()
    {
        _controller.ControllerContext = CreateContextWithUser();
        var newTitle = TestValues.NewChatTitle();
        _chatsServiceMock
            .Setup(s => s.UpdateChatTitleAsync(TestUserId, TestChatId, newTitle, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var patch = new ChatPatchRequest(newTitle);

        var result = await _controller.PatchChatAsync(TestChatId, patch, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task PatchChatAsync_WhenNotFound_ReturnsNotFound()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatsServiceMock
            .Setup(s => s.UpdateChatTitleAsync(TestUserId, MissingChatId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());
        var patch = new ChatPatchRequest(TestValues.NewChatTitle());

        var result = await _controller.PatchChatAsync(MissingChatId, patch, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteChatAsync_ReturnsNoContent()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatsServiceMock
            .Setup(s => s.DeleteChatAsync(TestUserId, TestChatId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _controller.DeleteChatAsync(TestChatId, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteChatAsync_WhenNotFound_ReturnsNotFound()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatsServiceMock
            .Setup(s => s.DeleteChatAsync(TestUserId, MissingChatId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.DeleteChatAsync(MissingChatId, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task PostMessageAsync_WhenInputIsEmpty_ReturnsBadRequest()
    {
        _controller.ControllerContext = CreateContextWithUser();
        var request = new ChatRequest(string.Empty);

        var result = await _controller.PostMessageAsync(TestChatId, request, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PostMessageAsync_WhenInputIsWhitespace_ReturnsBadRequest()
    {
        _controller.ControllerContext = CreateContextWithUser();
        var request = new ChatRequest("   ");

        var result = await _controller.PostMessageAsync(TestChatId, request, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PostMessageAsync_WhenInputIsValid_ReturnsOkWithResponse()
    {
        _controller.ControllerContext = CreateContextWithUser();
        var input = TestValues.NewMessageText();
        var output = TestValues.NewMessageText();
        _chatsServiceMock
            .Setup(s => s.CompleteChatAsync(TestUserId, TestChatId, input, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestChatId, output));
        var request = new ChatRequest(input);

        var result = await _controller.PostMessageAsync(TestChatId, request, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ChatResponse>(ok.Value);
        Assert.Equal(output, response.Output);
        Assert.Equal(TestChatId, response.ChatId);
    }

    [Fact]
    public async Task PostMessageAsync_WhenNotFound_ReturnsNotFound()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatsServiceMock
            .Setup(s => s.CompleteChatAsync(TestUserId, MissingChatId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());
        var request = new ChatRequest(TestValues.NewMessageText());

        var result = await _controller.PostMessageAsync(MissingChatId, request, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task PostMessageStreamAsync_WhenInputIsEmpty_Returns400()
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var request = new ChatRequest(string.Empty);

        await _controller.PostMessageStreamAsync(TestChatId, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status400BadRequest, _controller.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task PostMessageStreamAsync_WhenInputIsWhitespace_Returns400()
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var request = new ChatRequest("   ");

        await _controller.PostMessageStreamAsync(TestChatId, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status400BadRequest, _controller.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task PostMessageStreamAsync_WhenInputIsValid_WritesEventStream()
    {
        var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", TestUserId)]));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var input = TestValues.NewMessageText();
        var delta = TestValues.NewMessageText();
        _chatsServiceMock
            .Setup(s => s.StreamChatAsync(TestUserId, TestChatId, input, It.IsAny<CancellationToken>()))
            .Returns(SingleDelta(delta, TestContext.Current.CancellationToken));

        await _controller.PostMessageStreamAsync(TestChatId, new ChatRequest(input), TestContext.Current.CancellationToken);

        Assert.Equal("text/event-stream", _controller.HttpContext.Response.ContentType);
        responseBody.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(responseBody).ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(delta, body, StringComparison.Ordinal);
        Assert.Contains("[DONE]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostMessageStreamAsync_WritesCorrectSseJsonFormat()
    {
        var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", TestUserId)]));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var input = TestValues.NewMessageText();
        var delta = TestValues.NewMessageText();
        _chatsServiceMock
            .Setup(s => s.StreamChatAsync(TestUserId, TestChatId, input, It.IsAny<CancellationToken>()))
            .Returns(SingleDelta(delta, TestContext.Current.CancellationToken));

        await _controller.PostMessageStreamAsync(TestChatId, new ChatRequest(input), TestContext.Current.CancellationToken);

        responseBody.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(responseBody).ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains($"data: {{\"delta\":{{\"content\":\"{delta}\"}}}}", body, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostMessageStreamAsync_WhenServiceThrowsKeyNotFoundException_Returns404()
    {
        var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", TestUserId)]));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var input = TestValues.NewMessageText();
        _chatsServiceMock
            .Setup(s => s.StreamChatAsync(TestUserId, TestChatId, input, It.IsAny<CancellationToken>()))
            .Returns(new KeyNotFoundAsyncEnumerable());

        await _controller.PostMessageStreamAsync(TestChatId, new ChatRequest(input), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status404NotFound, _controller.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task GetChatsAsync_WhenSubClaimMissing_ThrowsInvalidOperationException()
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _controller.GetChatsAsync(TestContext.Current.CancellationToken));
    }

    private static async IAsyncEnumerable<string> SingleDelta(string value, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return value;
    }

    private static ControllerContext CreateContextWithUser()
    {
        var identity = new ClaimsIdentity([new Claim("sub", TestUserId)]);
        var user = new ClaimsPrincipal(identity);
        return new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
    }

    private sealed class KeyNotFoundAsyncEnumerable : IAsyncEnumerable<string>
    {
        public IAsyncEnumerator<string> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator();

        private sealed class Enumerator : IAsyncEnumerator<string>
        {
            public string Current => throw new InvalidOperationException(
                $"{nameof(MoveNextAsync)} always throws, so {nameof(Current)} is never reachable.");

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            public ValueTask<bool> MoveNextAsync() => throw new KeyNotFoundException();
        }
    }
}