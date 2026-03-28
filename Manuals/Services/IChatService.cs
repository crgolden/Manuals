namespace Manuals.Services;

public interface IChatService
{
    Task<(string? Id, string? OutputText)> CompleteChatAsync(
        string inputTextContent,
        string? previousResponseId = null,
        CancellationToken cancellationToken = default);

    Task StreamChatAsync(
        string inputTextContent,
        HttpResponse httpResponse,
        string? previousResponseId = null,
        CancellationToken cancellationToken = default);
}
