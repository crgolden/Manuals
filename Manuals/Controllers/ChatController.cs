namespace Manuals.Controllers;

using Microsoft.AspNetCore.Mvc;
using Models;
using Services;

[ApiController]
[Route("api/[controller]")]
public sealed class ChatController : ControllerBase
{
    private readonly IChatService _chatService;
    private readonly IConversationHistoryStore _conversationHistoryStore;

    public ChatController(IChatService chatService, IConversationHistoryStore conversationHistoryStore)
    {
        _chatService = chatService;
        _conversationHistoryStore = conversationHistoryStore;
    }

    [HttpPost]
    [ProducesResponseType<ChatResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PostAsync([FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        if (IsNullOrWhiteSpace(request.ConversationId) || IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest("ConversationId and Message are required.");
        }

        var response = await _chatService.CompleteChatAsync(request, cancellationToken);
        return Ok(response);
    }

    [HttpDelete("{conversationId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Delete([FromRoute] string conversationId, CancellationToken cancellationToken)
    {
        _conversationHistoryStore.Clear(conversationId);
        return NoContent();
    }
}
