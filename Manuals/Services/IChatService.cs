namespace Manuals.Services;

using Models;

public interface IChatService
{
    Task<IReadOnlyList<string>> GetConversationsAsync(
        string email,
        CancellationToken cancellationToken = default);

    Task<ConversationDetails> GetConversationAsync(
        string email,
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationItemSummary>> GetConversationItemsAsync(
        string email,
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<string> CreateConversationAsync(
        string email,
        CancellationToken cancellationToken = default);

    Task<(string? ConversationId, string? OutputText)> CompleteChatAsync(
        string email,
        string inputTextContent,
        string? conversationId = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamChatAsync(
        string email,
        string inputTextContent,
        string? conversationId = null,
        CancellationToken cancellationToken = default);

    Task DeleteConversationAsync(
        string email,
        string conversationId,
        CancellationToken cancellationToken = default);
}
