using OpenAI.Chat;

namespace Manuals.Services;

public interface IConversationHistoryStore
{
    IReadOnlyList<ChatMessage> GetHistory(string conversationId);
    void AddUserMessage(string conversationId, string message);
    void AddAssistantMessage(string conversationId, string message);
    void Clear(string conversationId);
}
