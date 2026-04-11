namespace Manuals.Tests.Controllers;

using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Manuals.Controllers;
using Manuals.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Models;
using Moq;

[Trait("Category", "Unit")]
public sealed class ChatsControllerTests
{
    private const string TestEmail = "test@example.com";
    private static readonly Guid TestChatId = new("aabbccdd-1122-3344-5566-778899aabbcc");
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
        IReadOnlyList<Chat> chats =
        [
            new Chat(new Guid("11111111-1111-1111-1111-111111111111"), "First Chat", 1_700_000_000L),
            new Chat(new Guid("22222222-2222-2222-2222-222222222222"), "Second Chat", 1_699_999_000L),
        ];
        _chatsServiceMock
            .Setup(s => s.GetChatsAsync(TestEmail, It.IsAny<CancellationToken>()))
            .ReturnsAsync(chats);

        var result = await _controller.GetChatsAsync(TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsType<IReadOnlyList<Chat>>(ok.Value, exactMatch: false);
        Assert.Equal(2, list.Count);
        Assert.Equal("First Chat", list[0].Title);
    }

    [Fact]
    public async Task GetChatsAsync_WhenEmpty_ReturnsOkWithEmptyList()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatsServiceMock
            .Setup(s => s.GetChatsAsync(TestEmail, It.IsAny<CancellationToken>()))
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
        var chat = new Chat(TestChatId, "My Chat", 1_700_000_000L);
        _chatsServiceMock
            .Setup(s => s.GetChatAsync(TestEmail, TestChatId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(chat);

        var result = await _controller.GetChatAsync(TestChatId, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsType<Chat>(ok.Value);
        Assert.Equal(TestChatId, returned.ChatId);
        Assert.Equal("My Chat", returned.Title);
        Assert.Equal(1_700_000_000L, returned.CreatedAt);
    }

    [Fact]
    public async Task GetChatAsync_WhenNotFound_ReturnsNotFound()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatsServiceMock
            .Setup(s => s.GetChatAsync(TestEmail, MissingChatId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.GetChatAsync(MissingChatId, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetChatMessagesAsync_ReturnsOkWithMessages()
    {
        _controller.ControllerContext = CreateContextWithUser();
        IReadOnlyList<ChatHistoryMessage> messages =
        [
            new ChatHistoryMessage("user", "Hello"),
            new ChatHistoryMessage("assistant", "Hi there!"),
        ];
        _chatsServiceMock
            .Setup(s => s.GetChatMessagesAsync(TestEmail, TestChatId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(messages);

        var result = await _controller.GetChatMessagesAsync(TestChatId, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsType<IReadOnlyList<ChatHistoryMessage>>(ok.Value, exactMatch: false);
        Assert.Equal(2, returned.Count);
        Assert.Equal("user", returned[0].Role);
        Assert.Equal("Hi there!", returned[1].Text);
    }

    [Fact]
    public async Task GetChatMessagesAsync_WhenNotFound_ReturnsNotFound()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatsServiceMock
            .Setup(s => s.GetChatMessagesAsync(TestEmail, MissingChatId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.GetChatMessagesAsync(MissingChatId, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task PostChatAsync_ReturnsCreatedAtActionWithChat()
    {
        // NOTE: This test verifies the shape of the IActionResult returned by the
        // action method. It does NOT exercise CreatedAtActionResult.OnFormatting
        // (route URL generation), because the action is called directly without the
        // full HTTP middleware pipeline. Actual Location header generation is covered
        // by the nightly CreateChatAsync helper, which asserts the header on a live
        // HTTP response through the real pipeline.
        _controller.ControllerContext = CreateContextWithUser();
        var chat = new Chat(TestChatId, null, 1_700_000_000L);
        _chatsServiceMock
            .Setup(s => s.CreateChatAsync(TestEmail, It.IsAny<CancellationToken>()))
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
        _chatsServiceMock
            .Setup(s => s.UpdateChatTitleAsync(TestEmail, TestChatId, "New Title", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var patch = new ChatPatchRequest("New Title");

        var result = await _controller.PatchChatAsync(TestChatId, patch, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task PatchChatAsync_WhenNotFound_ReturnsNotFound()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatsServiceMock
            .Setup(s => s.UpdateChatTitleAsync(TestEmail, MissingChatId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());
        var patch = new ChatPatchRequest("New Title");

        var result = await _controller.PatchChatAsync(MissingChatId, patch, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteChatAsync_ReturnsNoContent()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatsServiceMock
            .Setup(s => s.DeleteChatAsync(TestEmail, TestChatId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _controller.DeleteChatAsync(TestChatId, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteChatAsync_WhenNotFound_ReturnsNotFound()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatsServiceMock
            .Setup(s => s.DeleteChatAsync(TestEmail, MissingChatId, It.IsAny<CancellationToken>()))
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
        _chatsServiceMock
            .Setup(s => s.CompleteChatAsync(TestEmail, TestChatId, "Hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync((TestChatId, "Hi there!"));
        var request = new ChatRequest("Hello");

        var result = await _controller.PostMessageAsync(TestChatId, request, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ChatResponse>(ok.Value);
        Assert.Equal("Hi there!", response.Output);
        Assert.Equal(TestChatId, response.ChatId);
    }

    [Fact]
    public async Task PostMessageAsync_WhenNotFound_ReturnsNotFound()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatsServiceMock
            .Setup(s => s.CompleteChatAsync(TestEmail, MissingChatId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());
        var request = new ChatRequest("Hello");

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
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", TestEmail)]));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        _chatsServiceMock
            .Setup(s => s.StreamChatAsync(TestEmail, TestChatId, "Hello", It.IsAny<CancellationToken>()))
            .Returns(SingleDelta("Hello", TestContext.Current.CancellationToken));

        await _controller.PostMessageStreamAsync(TestChatId, new ChatRequest("Hello"), TestContext.Current.CancellationToken);

        Assert.Equal("text/event-stream", _controller.HttpContext.Response.ContentType);
        responseBody.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(responseBody).ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Hello", body);
        Assert.Contains("[DONE]", body);
    }

    [Fact]
    public async Task PostMessageStreamAsync_WritesCorrectSseJsonFormat()
    {
        var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", TestEmail)]));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        _chatsServiceMock
            .Setup(s => s.StreamChatAsync(TestEmail, TestChatId, "Hello", It.IsAny<CancellationToken>()))
            .Returns(SingleDelta("world", TestContext.Current.CancellationToken));

        await _controller.PostMessageStreamAsync(TestChatId, new ChatRequest("Hello"), TestContext.Current.CancellationToken);

        responseBody.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(responseBody).ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("data: {\"delta\":{\"content\":\"world\"}}", body);
        Assert.EndsWith("data: [DONE]\n\n", body);
    }

    private static async IAsyncEnumerable<string> SingleDelta(string value, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return value;
    }

    private static ControllerContext CreateContextWithUser(string email = TestEmail)
    {
        var identity = new ClaimsIdentity([new Claim("email", email)]);
        var user = new ClaimsPrincipal(identity);
        return new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
    }
}
