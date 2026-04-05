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
public sealed class ChatControllerTests
{
    private const string TestEmail = "test@example.com";

    private readonly Mock<IChatService> _chatServiceMock = new();
    private readonly ChatController _controller;

    public ChatControllerTests()
    {
        _controller = new ChatController(_chatServiceMock.Object);
    }

    [Fact]
    public async Task PostAsync_WhenInputIsEmpty_ReturnsBadRequest()
    {
        var request = new ChatRequest(string.Empty);

        var result = await _controller.PostAsync(request, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PostAsync_WhenInputIsWhitespace_ReturnsBadRequest()
    {
        var request = new ChatRequest("   ");

        var result = await _controller.PostAsync(request, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PostAsync_WhenInputIsValid_ReturnsOkWithChatResponse()
    {
        _controller.ControllerContext = CreateContextWithUser();
        var request = new ChatRequest("Hello");
        _chatServiceMock
            .Setup(s => s.CompleteChatAsync(TestEmail, "Hello", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(("conv-123", "Hi there!"));

        var result = await _controller.PostAsync(request, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ChatResponse>(ok.Value);
        Assert.Equal("Hi there!", response.Output);
        Assert.Equal("conv-123", response.ConversationId);
    }

    [Fact]
    public async Task PostAsync_WhenConversationIdProvided_PassesThroughAndEchosBack()
    {
        _controller.ControllerContext = CreateContextWithUser();
        var request = new ChatRequest("Hello", "conv-existing");
        _chatServiceMock
            .Setup(s => s.CompleteChatAsync(TestEmail, "Hello", "conv-existing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(("conv-existing", "Hi there!"));

        var result = await _controller.PostAsync(request, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ChatResponse>(ok.Value);
        Assert.Equal("conv-existing", response.ConversationId);
    }

    [Fact]
    public async Task GetConversationsAsync_ReturnsOkWithList()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatServiceMock
            .Setup(s => s.GetConversationsAsync(TestEmail, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["conv-1", "conv-2"]);

        var result = await _controller.GetConversationsAsync(TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IReadOnlyList<string>>(ok.Value);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task GetConversationsAsync_WhenEmpty_ReturnsOkWithEmptyList()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatServiceMock
            .Setup(s => s.GetConversationsAsync(TestEmail, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await _controller.GetConversationsAsync(TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IReadOnlyList<string>>(ok.Value);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetConversationAsync_ReturnsOkWithDetails()
    {
        _controller.ControllerContext = CreateContextWithUser();
        var details = new ConversationDetails("conv-abc", 1_700_000_000L);
        _chatServiceMock
            .Setup(s => s.GetConversationAsync(TestEmail, "conv-abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);

        var result = await _controller.GetConversationAsync("conv-abc", TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsType<ConversationDetails>(ok.Value);
        Assert.Equal("conv-abc", returned.ConversationId);
        Assert.Equal(1_700_000_000L, returned.CreatedAt);
    }

    [Fact]
    public async Task GetConversationAsync_WhenNotFound_ReturnsNotFound()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatServiceMock
            .Setup(s => s.GetConversationAsync(TestEmail, "conv-missing", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.GetConversationAsync("conv-missing", TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetConversationItemsAsync_ReturnsOkWithItems()
    {
        _controller.ControllerContext = CreateContextWithUser();
        IReadOnlyList<ConversationItemSummary> items =
        [
            new ConversationItemSummary("item-1", "user", "Hello"),
            new ConversationItemSummary("item-2", "assistant", "Hi there!"),
        ];
        _chatServiceMock
            .Setup(s => s.GetConversationItemsAsync(TestEmail, "conv-abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);

        var result = await _controller.GetConversationItemsAsync("conv-abc", TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsAssignableFrom<IReadOnlyList<ConversationItemSummary>>(ok.Value);
        Assert.Equal(2, returned.Count);
        Assert.Equal("user", returned[0].Role);
        Assert.Equal("Hi there!", returned[1].Text);
    }

    [Fact]
    public async Task GetConversationItemsAsync_WhenEmpty_ReturnsOkWithEmptyList()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatServiceMock
            .Setup(s => s.GetConversationItemsAsync(TestEmail, "conv-abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await _controller.GetConversationItemsAsync("conv-abc", TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IReadOnlyList<ConversationItemSummary>>(ok.Value);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetConversationItemsAsync_WhenNotFound_ReturnsNotFound()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatServiceMock
            .Setup(s => s.GetConversationItemsAsync(TestEmail, "conv-missing", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.GetConversationItemsAsync("conv-missing", TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task PostConversationAsync_ReturnsCreatedAtActionWithConversationId()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatServiceMock
            .Setup(s => s.CreateConversationAsync(TestEmail, It.IsAny<CancellationToken>()))
            .ReturnsAsync("conv-abc");

        var result = await _controller.PostConversationAsync(TestContext.Current.CancellationToken);

        var created = Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(nameof(ChatController.GetConversationAsync), created.ActionName);
        var json = JsonSerializer.Serialize(created.Value);
        Assert.Contains("conv-abc", json);
    }

    [Fact]
    public async Task DeleteConversationAsync_WhenNotFound_ReturnsNotFound()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatServiceMock
            .Setup(s => s.DeleteConversationAsync(TestEmail, "conv-missing", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var result = await _controller.DeleteConversationAsync("conv-missing", TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteConversationAsync_ReturnsNoContent()
    {
        _controller.ControllerContext = CreateContextWithUser();
        _chatServiceMock
            .Setup(s => s.DeleteConversationAsync(TestEmail, "conv-abc", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _controller.DeleteConversationAsync("conv-abc", TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task PostStreamAsync_WhenInputIsEmpty_Returns400()
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var request = new ChatRequest(string.Empty);

        await _controller.PostStreamAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status400BadRequest, _controller.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task PostStreamAsync_WhenInputIsWhitespace_Returns400()
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var request = new ChatRequest("   ");

        await _controller.PostStreamAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status400BadRequest, _controller.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task PostStreamAsync_WhenInputIsValid_WritesEventStream()
    {
        var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", TestEmail)]));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        _chatServiceMock
            .Setup(s => s.StreamChatAsync(TestEmail, "Hello", null, It.IsAny<CancellationToken>()))
            .Returns(SingleDelta("Hello", TestContext.Current.CancellationToken));

        var request = new ChatRequest("Hello");

        await _controller.PostStreamAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("text/event-stream", _controller.HttpContext.Response.ContentType);
        responseBody.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(responseBody).ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Hello", body);
        Assert.Contains("[DONE]", body);
    }

    [Fact]
    public async Task PostStreamAsync_WhenConversationIdProvided_PassesItToService()
    {
        var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", TestEmail)]));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        _chatServiceMock
            .Setup(s => s.StreamChatAsync(TestEmail, "Hello", "conv-123", It.IsAny<CancellationToken>()))
            .Returns(SingleDelta("Hi", TestContext.Current.CancellationToken));

        await _controller.PostStreamAsync(new ChatRequest("Hello", "conv-123"), TestContext.Current.CancellationToken);

        _chatServiceMock.Verify(s => s.StreamChatAsync(TestEmail, "Hello", "conv-123", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostStreamAsync_WhenInputIsValid_WritesSseJsonFormat()
    {
        var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = responseBody;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", TestEmail)]));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        _chatServiceMock
            .Setup(s => s.StreamChatAsync(TestEmail, "Hello", null, It.IsAny<CancellationToken>()))
            .Returns(SingleDelta("world", TestContext.Current.CancellationToken));

        await _controller.PostStreamAsync(new ChatRequest("Hello"), TestContext.Current.CancellationToken);

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
