using Manuals.Services;
using OpenAI.Chat;

namespace Manuals.Tests.Services;

public sealed class InMemoryConversationHistoryStoreTests
{
    private readonly InMemoryConversationHistoryStore _sut = new();

    [Fact]
    public void GetHistory_ReturnsEmpty_WhenConversationNotFound()
    {
        var history = _sut.GetHistory("unknown");

        Assert.Empty(history);
    }

    [Fact]
    public void AddUserMessage_ThenGetHistory_ReturnsSingleUserMessage()
    {
        _sut.AddUserMessage("conv-1", "Hello");

        var history = _sut.GetHistory("conv-1");

        Assert.Single(history);
        Assert.IsType<UserChatMessage>(history[0]);
    }

    [Fact]
    public void AddAssistantMessage_AppendsToHistory()
    {
        _sut.AddUserMessage("conv-2", "Hello");
        _sut.AddAssistantMessage("conv-2", "Hi there");

        var history = _sut.GetHistory("conv-2");

        Assert.Equal(2, history.Count);
        Assert.IsType<UserChatMessage>(history[0]);
        Assert.IsType<AssistantChatMessage>(history[1]);
    }

    [Fact]
    public void Clear_RemovesConversationHistory()
    {
        _sut.AddUserMessage("conv-3", "Hello");
        _sut.Clear("conv-3");

        var history = _sut.GetHistory("conv-3");

        Assert.Empty(history);
    }

    [Fact]
    public void Clear_NonexistentConversation_DoesNotThrow()
    {
        var exception = Record.Exception(() => _sut.Clear("does-not-exist"));

        Assert.Null(exception);
    }

    [Fact]
    public void MultipleConversations_AreIsolated()
    {
        _sut.AddUserMessage("conv-a", "Message A");
        _sut.AddUserMessage("conv-b", "Message B");

        var historyA = _sut.GetHistory("conv-a");
        var historyB = _sut.GetHistory("conv-b");

        Assert.Single(historyA);
        Assert.Single(historyB);
        _sut.Clear("conv-a");
        Assert.Empty(_sut.GetHistory("conv-a"));
        Assert.Single(_sut.GetHistory("conv-b"));
    }
}
