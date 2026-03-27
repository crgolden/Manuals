using Manuals.Models;
using Manuals.Services;
using Microsoft.AspNetCore.SignalR;
using System.Text;

namespace Manuals.Hubs;

public sealed class ChatHub : Hub
{
    private const int BufferThreshold = 20;

    private readonly IChatService _chatService;

    public ChatHub(IChatService chatService)
    {
        _chatService = chatService;
    }

    /// <summary>
    /// Initiates a streaming chat completion. The client receives ReceiveSignal events:
    /// { Type="typing" }  — AI is processing
    /// { Type="partial", Content }  — incremental text chunk
    /// { Type="completed", Content }  — full assembled response
    /// </summary>
    public async Task SendMessage(ChatRequest request)
    {
        var conversationId = request.ConversationId;

        await Clients.Caller.SendAsync("ReceiveSignal", new
        {
            Type = "typing",
            ConversationId = conversationId
        });

        var buffer = new StringBuilder();
        var fullMessage = new StringBuilder();

        await foreach (var chunk in _chatService.StreamChatAsync(request, Context.ConnectionAborted))
        {
            buffer.Append(chunk);
            fullMessage.Append(chunk);

            if (buffer.Length >= BufferThreshold)
            {
                await Clients.Caller.SendAsync("ReceiveSignal", new
                {
                    Type = "partial",
                    ConversationId = conversationId,
                    Content = buffer.ToString()
                });
                buffer.Clear();
            }
        }

        // Flush any remaining buffered content
        if (buffer.Length > 0)
        {
            await Clients.Caller.SendAsync("ReceiveSignal", new
            {
                Type = "partial",
                ConversationId = conversationId,
                Content = buffer.ToString()
            });
        }

        await Clients.Caller.SendAsync("ReceiveSignal", new
        {
            Type = "completed",
            ConversationId = conversationId,
            Content = fullMessage.ToString()
        });
    }
}
