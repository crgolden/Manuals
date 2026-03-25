using System.Collections.Concurrent;
using OpenAI.Chat;

namespace Manuals.Services;

public sealed class InMemoryConversationHistoryStore : IConversationHistoryStore
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _store = new();

    public IReadOnlyList<ChatMessage> GetHistory(string conversationId)
        => _store.TryGetValue(conversationId, out var history)
            ? history.AsReadOnly()
            : Array.Empty<ChatMessage>();

    public void AddUserMessage(string conversationId, string message)
        => GetOrCreate(conversationId).Add(new UserChatMessage(message));

    public void AddAssistantMessage(string conversationId, string message)
        => GetOrCreate(conversationId).Add(new AssistantChatMessage(message));

    public void Clear(string conversationId)
        => _store.TryRemove(conversationId, out _);

    private List<ChatMessage> GetOrCreate(string conversationId)
        => _store.GetOrAdd(conversationId, _ => []);
}
