namespace Manuals.Models;

public sealed record ChatRequest(string Input, string? ConversationId = null);
