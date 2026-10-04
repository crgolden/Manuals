namespace Manuals.Tests.Integration.TestSupport;

using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using Manuals.Controllers;
using OpenAI;
using OpenAI.Responses;

public sealed class OpenAIResponsesStub : HttpMessageHandler
{
    private readonly ConcurrentQueue<CreateResponseOptions> _requests = new();

    public string ReplyText { get; } = Generated.NewMessageText();

    public IReadOnlyList<string> StreamedDeltas { get; } = [Generated.NewMessageText(), Generated.NewMessageText()];

    public IReadOnlyList<CreateResponseOptions> Requests => [.. _requests];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not { } content)
        {
            throw new InvalidOperationException("The Responses API stub received a request with no body.");
        }

        var body = BinaryData.FromBytes(await content.ReadAsByteArrayAsync(cancellationToken));
        var options = ModelReaderWriter.Read<CreateResponseOptions>(body, ModelReaderWriterOptions.Json, OpenAIContext.Default)
            ?? throw new InvalidOperationException("The Responses API stub could not read the request body as CreateResponseOptions.");
        _requests.Enqueue(options);

        return options.StreamingEnabled is true ? StreamedReply() : CompletedReply();
    }

    private static string ServerSentEvent(StreamingResponseUpdate update) =>
        ChatsController.SseDataPrefix + ModelReaderWriter.Write(update, ModelReaderWriterOptions.Json) + ChatsController.SseEventTerminator;

    private HttpResponseMessage CompletedReply()
    {
        var response = new ResponseResult
        {
            Id = Generated.NewResponseId(),
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(Generated.NewUnixSeconds()),
            Status = ResponseStatus.Completed,
            Model = Generated.NewModelName(),
        };
        response.OutputItems.Add(ResponseItem.CreateAssistantMessageItem(ReplyText));
        var json = ModelReaderWriter.Write(response, ModelReaderWriterOptions.Json);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(json.ToArray())
            {
                Headers = { ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json) },
            },
        };
    }

    private HttpResponseMessage StreamedReply()
    {
        var events = new StringBuilder();
        foreach (var delta in StreamedDeltas)
        {
            events.Append(ServerSentEvent(new StreamingResponseOutputTextDeltaUpdate { Delta = delta }));
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(events.ToString(), Encoding.UTF8, MediaTypeNames.Text.EventStream),
        };
    }
}
