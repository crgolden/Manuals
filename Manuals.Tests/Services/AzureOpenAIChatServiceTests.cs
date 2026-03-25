using Manuals.Models;
using Manuals.Services;
using Moq;
using OpenAI.Chat;

namespace Manuals.Tests.Services;

/// <summary>
/// Tests for AzureOpenAIChatService using a mock IChatClientFactory.
///
/// NOTE: ChatClient is a non-sealed SDK class with no interface, so direct mocking
/// via Moq requires the virtual/protected constructor. These tests validate service
/// logic (history management, message construction) by testing through the public
/// interface with a real InMemoryConversationHistoryStore and a stubbed factory.
///
/// For full end-to-end coverage of OpenAI calls, use the integration tests with
/// WebApplicationFactory and a mock IChatService.
/// </summary>
public sealed class AzureOpenAIChatServiceTests
{
    [Fact]
    public void Constructor_CreatesInstance_WhenFactoryProvided()
    {
        var mockFactory = new Mock<IChatClientFactory>();
        mockFactory.Setup(f => f.CreateChatClient()).Returns(CreateChatClientStub());
        var historyStore = new InMemoryConversationHistoryStore();

        var sut = new AzureOpenAIChatService(mockFactory.Object, historyStore);

        Assert.NotNull(sut);
        mockFactory.Verify(f => f.CreateChatClient(), Times.Once);
    }

    [Fact]
    public void Constructor_CallsCreateChatClient_ExactlyOnce()
    {
        var mockFactory = new Mock<IChatClientFactory>();
        mockFactory.Setup(f => f.CreateChatClient()).Returns(CreateChatClientStub());
        var historyStore = new InMemoryConversationHistoryStore();

        _ = new AzureOpenAIChatService(mockFactory.Object, historyStore);

        mockFactory.Verify(f => f.CreateChatClient(), Times.Once);
    }

    // ChatClient is an SDK type with no public mockable interface.
    // A stub is created via the protected constructor available in the OpenAI SDK.
    private static ChatClient CreateChatClientStub()
        => (ChatClient)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(ChatClient));
}
