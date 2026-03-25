using Manuals.Models;
using OpenAI.Chat;
using System.Runtime.CompilerServices;
using System.Text;

namespace Manuals.Services;

public sealed class AzureOpenAIChatService : IChatService
{
    private readonly ChatClient _chatClient;
    private readonly IConversationHistoryStore _historyStore;

    public AzureOpenAIChatService(
        IChatClientFactory chatClientFactory,
        IConversationHistoryStore historyStore)
    {
        _chatClient = chatClientFactory.CreateChatClient();
        _historyStore = historyStore;
    }

    public async Task<ChatResponse> CompleteChatAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages(request);
        var result = await _chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        var completion = result.Value;
        var assistantText = completion.Content[0].Text;

        _historyStore.AddUserMessage(request.ConversationId, request.Message);
        _historyStore.AddAssistantMessage(request.ConversationId, assistantText);

        return new ChatResponse(
            request.ConversationId,
            assistantText,
            completion.FinishReason.ToString());
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages(request);
        var completionUpdates = _chatClient.CompleteChatStreamingAsync(messages, cancellationToken: cancellationToken);
        var fullResponse = new StringBuilder();

        await foreach (var update in completionUpdates.WithCancellation(cancellationToken))
        {
            foreach (var contentPart in update.ContentUpdate)
            {
                var text = contentPart.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    fullResponse.Append(text);
                    yield return text;
                }
            }
        }

        _historyStore.AddUserMessage(request.ConversationId, request.Message);
        _historyStore.AddAssistantMessage(request.ConversationId, fullResponse.ToString());
    }

    private List<ChatMessage> BuildMessages(ChatRequest request)
    {
        var history = _historyStore.GetHistory(request.ConversationId);
        var messages = new List<ChatMessage>();

        if (history.Count == 0 && !string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new SystemChatMessage(request.SystemPrompt));

        messages.AddRange(history);
        messages.Add(new UserChatMessage(request.Message));
        return messages;
    }
}
