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
public sealed class ChatsController : ControllerBase
{
    private readonly IChatsService _chatsService;

    public ChatsController(IChatsService chatsService)
    {
        _chatsService = chatsService;
    }

    private string Email => User.FindFirstValue("email") ?? throw new InvalidOperationException("Missing email.");

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<Chat>>(Status200OK)]
    public async Task<IActionResult> GetChatsAsync(CancellationToken cancellationToken)
    {
        var chats = await _chatsService.GetChatsAsync(Email, cancellationToken);
        return Ok(chats);
    }

    [HttpGet("{chatId:guid}")]
    [ProducesResponseType<Chat>(Status200OK)]
    [ProducesResponseType(Status404NotFound)]
    public async Task<IActionResult> GetChatAsync(Guid chatId, CancellationToken cancellationToken)
    {
        try
        {
            var chat = await _chatsService.GetChatAsync(Email, chatId, cancellationToken);
            return Ok(chat);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{chatId:guid}/messages")]
    [ProducesResponseType<IReadOnlyList<ChatHistoryMessage>>(Status200OK)]
    [ProducesResponseType(Status404NotFound)]
    public async Task<IActionResult> GetChatMessagesAsync(Guid chatId, CancellationToken cancellationToken)
    {
        try
        {
            var messages = await _chatsService.GetChatMessagesAsync(Email, chatId, cancellationToken);
            return Ok(messages);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    [ProducesResponseType<Chat>(Status201Created)]
    public async Task<IActionResult> PostChatAsync(CancellationToken cancellationToken)
    {
        var chat = await _chatsService.CreateChatAsync(Email, cancellationToken);
        return CreatedAtAction(nameof(GetChatAsync), new { chatId = chat.ChatId }, chat);
    }

    [HttpPatch("{chatId:guid}")]
    [Consumes("application/merge-patch+json")]
    [ProducesResponseType(Status204NoContent)]
    [ProducesResponseType(Status400BadRequest)]
    [ProducesResponseType(Status404NotFound)]
    public async Task<IActionResult> PatchChatAsync(Guid chatId, [FromBody] ChatPatchRequest patch, CancellationToken cancellationToken)
    {
        if (IsNullOrWhiteSpace(patch.Title))
        {
            ModelState.AddModelError(nameof(patch.Title), "Title is required.");
            return BadRequest(ModelState);
        }

        try
        {
            await _chatsService.UpdateChatTitleAsync(Email, chatId, patch.Title, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{chatId:guid}")]
    [ProducesResponseType(Status204NoContent)]
    [ProducesResponseType(Status404NotFound)]
    public async Task<IActionResult> DeleteChatAsync(Guid chatId, CancellationToken cancellationToken)
    {
        try
        {
            await _chatsService.DeleteChatAsync(Email, chatId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{chatId:guid}/messages")]
    [ProducesResponseType<ChatResponse>(Status200OK)]
    [ProducesResponseType(Status400BadRequest)]
    [ProducesResponseType(Status404NotFound)]
    public async Task<IActionResult> PostMessageAsync(Guid chatId, [FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        if (IsNullOrWhiteSpace(request.Input))
        {
            ModelState.AddModelError(nameof(request.Input), "Input is required.");
            return BadRequest(ModelState);
        }

        try
        {
            var response = await _chatsService.CompleteChatAsync(Email, chatId, request.Input, cancellationToken);
            return Ok(new ChatResponse(response.OutputText, response.ChatId));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{chatId:guid}/messages/stream")]
    [Produces(EventStream)]
    [ProducesResponseType(Status400BadRequest)]
    [ProducesResponseType(Status404NotFound)]
    public async Task PostMessageStreamAsync(Guid chatId, [FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        if (IsNullOrWhiteSpace(request.Input))
        {
            Response.StatusCode = Status400BadRequest;
            return;
        }

        try
        {
            HttpContext.Features.GetRequiredFeature<IHttpResponseBodyFeature>().DisableBuffering();
            Response.ContentType = EventStream;

            await foreach (var delta in _chatsService.StreamChatAsync(Email, chatId, request.Input, cancellationToken))
            {
                var json = JsonSerializer.Serialize(new { delta = new { content = delta } });
                await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }

            await Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            Response.StatusCode = Status404NotFound;
        }
    }
}
