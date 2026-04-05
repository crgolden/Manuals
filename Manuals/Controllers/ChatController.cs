namespace Manuals.Controllers;

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Models;
using Services;
using static System.Net.Mime.MediaTypeNames.Text;
using static StatusCodes;

[ApiController]
[Route("api/[controller]")]
[Authorize(nameof(Manuals))]
public sealed class ChatController : ControllerBase
{
    private readonly IChatService _chatService;

    public ChatController(IChatService chatService)
    {
        _chatService = chatService;
    }

    private string Email => User.FindFirstValue("email") ?? throw new InvalidOperationException("Missing email.");

    [HttpPost]
    [ProducesResponseType<ChatResponse>(Status200OK)]
    [ProducesResponseType(Status400BadRequest)]
    public async Task<IActionResult> PostAsync([FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        if (IsNullOrWhiteSpace(request.Input))
        {
            ModelState.AddModelError(nameof(request.Input), "Input is required.");
            return BadRequest(ModelState);
        }

        var response = await _chatService.CompleteChatAsync(Email, request.Input, request.ConversationId, cancellationToken);
        return Ok(new ChatResponse(response.OutputText, response.ConversationId));
    }

    [HttpGet("conversations")]
    [ProducesResponseType<IReadOnlyList<string>>(Status200OK)]
    public async Task<IActionResult> GetConversationsAsync(CancellationToken cancellationToken)
    {
        var conversations = await _chatService.GetConversationsAsync(Email, cancellationToken);
        return Ok(conversations);
    }

    [HttpGet("conversations/{conversationId}")]
    [ProducesResponseType<ConversationDetails>(Status200OK)]
    [ProducesResponseType(Status404NotFound)]
    public async Task<IActionResult> GetConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        try
        {
            var details = await _chatService.GetConversationAsync(Email, conversationId, cancellationToken);
            return Ok(details);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("conversations/{conversationId}/items")]
    [ProducesResponseType<IReadOnlyList<ConversationItemSummary>>(Status200OK)]
    [ProducesResponseType(Status404NotFound)]
    public async Task<IActionResult> GetConversationItemsAsync(string conversationId, CancellationToken cancellationToken)
    {
        try
        {
            var items = await _chatService.GetConversationItemsAsync(Email, conversationId, cancellationToken);
            return Ok(items);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("conversations")]
    [ProducesResponseType<ConversationDetails>(Status201Created)]
    public async Task<IActionResult> PostConversationAsync(CancellationToken cancellationToken)
    {
        var conversationId = await _chatService.CreateConversationAsync(Email, cancellationToken);
        var action = nameof(GetConversationAsync);
        return CreatedAtAction(action, new { conversationId }, new { ConversationId = conversationId });
    }

    [HttpDelete("conversations/{conversationId}")]
    [ProducesResponseType(Status204NoContent)]
    [ProducesResponseType(Status404NotFound)]
    public async Task<IActionResult> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        try
        {
            await _chatService.DeleteConversationAsync(Email, conversationId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("stream")]
    [Produces(EventStream)]
    [ProducesResponseType(Status400BadRequest)]
    public async Task PostStreamAsync([FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        if (IsNullOrWhiteSpace(request.Input))
        {
            Response.StatusCode = Status400BadRequest;
            return;
        }

        HttpContext.Features.GetRequiredFeature<IHttpResponseBodyFeature>().DisableBuffering();
        Response.ContentType = EventStream;

        await foreach (var delta in _chatService.StreamChatAsync(Email, request.Input, request.ConversationId, cancellationToken))
        {
            var json = JsonSerializer.Serialize(new { delta = new { content = delta } });
            await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }

        await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
    }
}
