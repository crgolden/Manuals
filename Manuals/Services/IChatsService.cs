namespace Manuals.Services;

using Models;

public interface IChatsService
{
    Task<IReadOnlyList<Chat>> GetChatsAsync(
        string email,
        CancellationToken cancellationToken = default);

    Task<Chat> GetChatAsync(
        string email,
        Guid chatId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatHistoryMessage>> GetChatMessagesAsync(
        string email,
        Guid chatId,
        CancellationToken cancellationToken = default);

    Task<Chat> CreateChatAsync(
        string email,
        CancellationToken cancellationToken = default);

    Task UpdateChatTitleAsync(
        string email,
        Guid chatId,
        string title,
        CancellationToken cancellationToken = default);

    Task DeleteChatAsync(
        string email,
        Guid chatId,
        CancellationToken cancellationToken = default);

    Task<(Guid ChatId, string? OutputText)> CompleteChatAsync(
        string email,
        Guid chatId,
        string inputText,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamChatAsync(
        string email,
        Guid chatId,
        string inputText,
        CancellationToken cancellationToken = default);
}
