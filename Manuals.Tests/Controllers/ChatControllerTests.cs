namespace Manuals.Tests.Controllers;

using Manuals.Controllers;
using Manuals.Models;
using Manuals.Services;
using Microsoft.AspNetCore.Mvc;
using Moq;

[Trait("Category", "Unit")]
public sealed class ChatControllerTests
{
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

        var result = await _controller.PostAsync(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PostAsync_WhenInputIsWhitespace_ReturnsBadRequest()
    {
        var request = new ChatRequest("   ");

        var result = await _controller.PostAsync(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PostAsync_WhenInputIsValid_ReturnsOkWithChatResponse()
    {
        var request = new ChatRequest("Hello");
        _chatServiceMock
            .Setup(s => s.CompleteChatAsync("Hello", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(("id-123", "Hi there!"));

        var result = await _controller.PostAsync(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ChatResponse>(ok.Value);
        Assert.Equal("Hi there!", response.Output);
        Assert.Equal("id-123", response.ResponseId);
    }
}
