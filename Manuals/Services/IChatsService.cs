namespace Manuals.Services;

using Models;

public interface IChatsService
{
    Task<IReadOnlyList<Chat>> GetChatsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<Chat> GetChatAsync(
        string userId,
        Guid chatId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatHistoryMessage>> GetChatMessagesAsync(
        string userId,
        Guid chatId,
        CancellationToken cancellationToken = default);

    Task<Chat> CreateChatAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task UpdateChatTitleAsync(
        string userId,
        Guid chatId,
        string title,
        CancellationToken cancellationToken = default);

    Task DeleteChatAsync(
        string userId,
        Guid chatId,
        CancellationToken cancellationToken = default);

    Task<(Guid ChatId, string? OutputText)> CompleteChatAsync(
        string userId,
        Guid chatId,
        string inputText,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamChatAsync(
        string userId,
        Guid chatId,
        string inputText,
        CancellationToken cancellationToken = default);
}
