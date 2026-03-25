using Manuals.Models;

namespace Manuals.Services;

public interface IChatService
{
    Task<ChatResponse> CompleteChatAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamChatAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default);
}
