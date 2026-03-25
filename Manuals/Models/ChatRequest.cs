namespace Manuals.Models;

public sealed record ChatRequest(
    string ConversationId,
    string Message,
    string? SystemPrompt = null
);
