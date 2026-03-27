namespace Manuals.Services;

using Models;
using OpenAI.Chat;
using System.Runtime.CompilerServices;
using System.Text;

public sealed class AzureOpenAIChatService : IChatService
{
    private readonly ChatClient _chatClient;
    private readonly IConversationHistoryStore _historyStore;

    public AzureOpenAIChatService(ChatClient chatClient, IConversationHistoryStore historyStore)
    {
        _chatClient = chatClient;
        _historyStore = historyStore;
    }

    public async Task<ChatResponse?> CompleteChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        if (IsNullOrWhiteSpace(request.ConversationId))
        {
            return null;
        }

        _historyStore.AddUserMessage(request.ConversationId, request.Message);
        var history = _historyStore.GetHistory(request.ConversationId);
        var messages = new List<ChatMessage>();
        if (history.Count == 0 && !IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new SystemChatMessage(request.SystemPrompt));
        }

        messages.AddRange(history);
        messages.Add(new UserChatMessage(request.Message));
        var result = await _chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        var finishReason = result?.Value?.FinishReason.ToString();
        if (IsNullOrWhiteSpace(finishReason))
        {
            return null;
        }

        if (result?.Value?.Content is null || result.Value.Content.Count == 0)
        {
            return null;
        }

        var assistantText = result.Value.Content[0].Text;
        if (!IsNullOrWhiteSpace(assistantText))
        {
            _historyStore.AddAssistantMessage(request.ConversationId, assistantText);
        }

        return new ChatResponse(request.ConversationId, assistantText, finishReason);
    }

    public async IAsyncEnumerable<string> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = _historyStore.GetHistory(request.ConversationId);
        var messages = new List<ChatMessage>();
        if (history.Count == 0 && !IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new SystemChatMessage(request.SystemPrompt));
        }

        messages.AddRange(history);
        messages.Add(new UserChatMessage(request.Message));
        var completionUpdates = _chatClient.CompleteChatStreamingAsync(messages, cancellationToken: cancellationToken);
        var fullResponse = new StringBuilder();
        await foreach (var chatMessageContent in completionUpdates.Select(x => x.ContentUpdate).WithCancellation(cancellationToken))
        {
            foreach (var text in chatMessageContent.Where(x => !IsNullOrWhiteSpace(x.Text)).Select(x => x.Text))
            {
                fullResponse.Append(text);
                yield return text;
            }
        }

        _historyStore.AddUserMessage(request.ConversationId, request.Message);
        _historyStore.AddAssistantMessage(request.ConversationId, fullResponse.ToString());
    }
}
