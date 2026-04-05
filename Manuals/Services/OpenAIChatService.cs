#pragma warning disable OPENAI001
namespace Manuals.Services;

using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Models;
using OpenAI.Conversations;
using OpenAI.Responses;
using StackExchange.Redis;
using static BinaryData;
using static System.ClientModel.BinaryContent;
using static System.Text.Json.JsonDocument;

public sealed class OpenAIChatService : IChatService
{
    private readonly ResponsesClient _responsesClient;
    private readonly ConversationClient _conversationClient;
    private readonly IDatabase _database;
    private readonly string _model;
    private readonly int _maxOutputTokenCount;

    public OpenAIChatService(
        ResponsesClient responsesClient,
        ConversationClient conversationClient,
        IDatabase database,
        IConfiguration configuration)
    {
        _responsesClient = responsesClient;
        _conversationClient = conversationClient;
        _database = database;
        _model = configuration.GetValue<string?>("OpenAIModel") ?? throw new InvalidOperationException("Invalid 'OpenAIModel'.");
        _maxOutputTokenCount = configuration.GetValue<int?>("OpenAIMaxOutputTokenCount") ?? throw new InvalidOperationException("Invalid 'OpenAIMaxOutputTokenCount'.");
    }

    public async Task<IReadOnlyList<string>> GetConversationsAsync(string email, CancellationToken cancellationToken = default)
    {
        var key = ConversationsKey(email);
        var members = await _database.SortedSetRangeByRankAsync(key, order: Order.Descending);
        return Array.ConvertAll(members, m => m.ToString());
    }

    public async Task<ConversationDetails> GetConversationAsync(string email, string conversationId, CancellationToken cancellationToken = default)
    {
        var score = await _database.SortedSetScoreAsync(ConversationsKey(email), conversationId);
        if (score is null)
        {
            throw new KeyNotFoundException($"Conversation '{conversationId}' not found for user.");
        }

        var result = await _conversationClient.GetConversationAsync(conversationId);
        using var doc = Parse(result.GetRawResponse().Content);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString() ?? throw new InvalidOperationException("Response missing 'id'.");
        var createdAt = root.GetProperty("created_at").GetInt64();
        return new ConversationDetails(id, createdAt);
    }

    public async Task<IReadOnlyList<ConversationItemSummary>> GetConversationItemsAsync(string email, string conversationId, CancellationToken cancellationToken = default)
    {
        var score = await _database.SortedSetScoreAsync(ConversationsKey(email), conversationId);
        if (score is null)
        {
            throw new KeyNotFoundException($"Conversation '{conversationId}' not found for user.");
        }

        var items = new List<ConversationItemSummary>();
        await foreach (var page in ((IAsyncEnumerable<ClientResult>)_conversationClient.GetConversationItemsAsync(conversationId)).WithCancellation(cancellationToken))
        {
            using var doc = Parse(page.GetRawResponse().Content);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                continue;
            }

            foreach (var element in data.EnumerateArray())
            {
                var id = element.GetProperty("id").GetString();
                var role = element.TryGetProperty("role", out var r) ? r.GetString() : null;
                var text = element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                    ? ExtractText(content)
                    : null;
                items.Add(new ConversationItemSummary(id!, role, text));
            }
        }

        return items;
    }

    public async Task<string> CreateConversationAsync(string email, CancellationToken cancellationToken = default)
    {
        const string data = "{}";
        var value = FromString(data);
        var content = Create(value);
        var result = await _conversationClient.CreateConversationAsync(content);
        var response = result.GetRawResponse();
        var utf8Json = response.Content;
        using var jsonDocument = Parse(utf8Json);
        var jsonElement = jsonDocument.RootElement.GetProperty("id");
        var id = jsonElement.GetString() ?? throw new InvalidOperationException("Conversation response did not contain an id.");
        await _database.SortedSetAddAsync(ConversationsKey(email), id, Score());
        return id;
    }

    public async Task<(string? ConversationId, string? OutputText)> CompleteChatAsync(
        string email,
        string inputTextContent,
        string? conversationId = null,
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
            MaxOutputTokenCount = _maxOutputTokenCount
        };
        if (!IsNullOrWhiteSpace(conversationId))
        {
            await _database.SortedSetAddAsync(ConversationsKey(email), conversationId, Score());
            options.ConversationOptions = new ResponseConversationOptions(conversationId);
        }

        var response = await _responsesClient.CreateResponseAsync(options, cancellationToken);
        var outputText = response?.Value?.GetOutputText() ?? throw new InvalidOperationException("OpenAI returned no output.");
        return (conversationId, outputText);
    }

    public IAsyncEnumerable<string> StreamChatAsync(
        string email,
        string inputTextContent,
        string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        if (IsNullOrWhiteSpace(inputTextContent))
        {
            throw new ArgumentNullException(nameof(inputTextContent));
        }

        return StreamChatAsyncCore(email, inputTextContent, conversationId, cancellationToken);
    }

    public async Task DeleteConversationAsync(
        string email,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (IsNullOrWhiteSpace(conversationId))
        {
            throw new ArgumentNullException(nameof(conversationId));
        }

        var key = ConversationsKey(email);
        var score = await _database.SortedSetScoreAsync(key, conversationId);
        if (score is null)
        {
            throw new KeyNotFoundException($"Conversation '{conversationId}' not found for user.");
        }

        await _conversationClient.DeleteConversationAsync(conversationId);
        await _database.SortedSetRemoveAsync(key, conversationId);
    }

    private static string ConversationsKey(string email) => $"user:{email}:conversations";

    private static double Score() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string? ExtractText(JsonElement contentArray)
    {
        foreach (var part in contentArray.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text))
            {
                return text.GetString();
            }
        }

        return null;
    }

    private async IAsyncEnumerable<string> StreamChatAsyncCore(
        string email,
        string inputTextContent,
        string? conversationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var inputItems = new[]
        {
            ResponseItem.CreateUserMessageItem(inputTextContent)
        };
        var options = new CreateResponseOptions(_model, inputItems)
        {
            MaxOutputTokenCount = _maxOutputTokenCount,
            StreamingEnabled = true
        };
        if (!IsNullOrWhiteSpace(conversationId))
        {
            await _database.SortedSetAddAsync(ConversationsKey(email), conversationId, Score());
            options.ConversationOptions = new ResponseConversationOptions(conversationId);
        }

        var updates = _responsesClient.CreateResponseStreamingAsync(options, cancellationToken);
        await foreach (var update in updates.OfType<StreamingResponseOutputTextDeltaUpdate>().WithCancellation(cancellationToken))
        {
            yield return update.Delta;
        }
    }
}
