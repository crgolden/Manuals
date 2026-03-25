using Manuals.Models;
using Manuals.Services;
using Microsoft.AspNetCore.Mvc;

namespace Manuals.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ChatController : ControllerBase
{
    private readonly IChatService _chatService;

    public ChatController(IChatService chatService)
    {
        _chatService = chatService;
    }

    /// <summary>
    /// Sends a message to Azure OpenAI and returns the complete response.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ChatResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PostAsync(
        [FromBody] ChatRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ConversationId) ||
            string.IsNullOrWhiteSpace(request.Message))
            return BadRequest("ConversationId and Message are required.");

        var response = await _chatService.CompleteChatAsync(request, cancellationToken);
        return Ok(response);
    }

    /// <summary>
    /// Clears the conversation history for the given conversation ID.
    /// </summary>
    [HttpDelete("{conversationId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Delete(
        [FromRoute] string conversationId,
        [FromServices] IConversationHistoryStore historyStore)
    {
        historyStore.Clear(conversationId);
        return NoContent();
    }
}
