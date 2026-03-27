namespace Manuals.Models;

public sealed record ChatResponse(
    string ConversationId,
    string Message,
    string FinishReason);
