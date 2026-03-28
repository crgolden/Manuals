#pragma warning disable OPENAI001
namespace Manuals.Services;

using System.Net.Mime;
using System.Text.Json;
using OpenAI.Responses;

public sealed class OpenAIChatService : IChatService
{
    private readonly ResponsesClient _responsesClient;
    private readonly string _model;

    public OpenAIChatService(ResponsesClient responsesClient, IConfiguration configuration)
    {
        _responsesClient = responsesClient;
        _model = configuration.GetValue<string?>("OpenAIModel") ?? throw new InvalidOperationException("Invalid 'OpenAIModel'.");
    }

    public async Task<(string? Id, string? OutputText)> CompleteChatAsync(
        string inputTextContent,
        string? previousResponseId = null,
        CancellationToken cancellationToken = default)
    {
        if (IsNullOrWhiteSpace(inputTextContent))
        {
            throw new ArgumentNullException(nameof(inputTextContent));
        }

        var inputItems = new[]
        {
            ResponseItem.CreateUserMessageItem(inputTextContent)
        };
        var options = new CreateResponseOptions(_model, inputItems)
        {
            MaxOutputTokenCount = 1000,
            PreviousResponseId = previousResponseId
        };
        var response = await _responsesClient.CreateResponseAsync(options, cancellationToken);
        return (response?.Value?.Id, response?.Value?.GetOutputText());
    }

    public async Task StreamChatAsync(
        string inputTextContent,
        HttpResponse httpResponse,
        string? previousResponseId = null,
        CancellationToken cancellationToken = default)
    {
        if (IsNullOrWhiteSpace(inputTextContent))
        {
            throw new ArgumentNullException(nameof(inputTextContent));
        }

        httpResponse.ContentType = MediaTypeNames.Text.EventStream;
        var inputItems = new[]
        {
            ResponseItem.CreateUserMessageItem(inputTextContent)
        };
        var options = new CreateResponseOptions(_model, inputItems)
        {
            MaxOutputTokenCount = 1000,
            PreviousResponseId = previousResponseId,
            StreamingEnabled = true
        };
        var updates = _responsesClient.CreateResponseStreamingAsync(options, cancellationToken);
        await foreach (var update in updates.OfType<StreamingResponseOutputTextDeltaUpdate>().WithCancellation(cancellationToken))
        {
            var value = new
            {
                delta = new
                {
                    content = update.Delta
                }
            };
            var json = JsonSerializer.Serialize(value);
            await httpResponse.WriteAsync($"data: {json}\n\n", cancellationToken);
            await httpResponse.Body.FlushAsync(cancellationToken);
        }

        await httpResponse.WriteAsync("data: [DONE]\n\n", cancellationToken);
    }
}
