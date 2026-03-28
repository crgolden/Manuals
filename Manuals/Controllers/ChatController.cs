namespace Manuals.Controllers;

using Microsoft.AspNetCore.Mvc;
using Models;
using Services;

[ApiController]
[Route("api/[controller]")]
public sealed class ChatController : ControllerBase
{
    private readonly IChatService _chatService;

    public ChatController(IChatService chatService)
    {
        _chatService = chatService;
    }

    [HttpPost]
    [ProducesResponseType<ChatResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PostAsync([FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        if (IsNullOrWhiteSpace(request.Input))
        {
            ModelState.AddModelError(nameof(request.Input), "Input is required.");
            return BadRequest(ModelState);
        }

        var response = await _chatService.CompleteChatAsync(request.Input, request.PreviousResponseId, cancellationToken);
        return Ok(new ChatResponse(response.OutputText, response.Id));
    }
}
