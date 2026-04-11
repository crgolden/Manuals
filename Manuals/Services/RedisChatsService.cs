#pragma warning disable OPENAI001
namespace Manuals.Services;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Models;
using OpenAI.Responses;
using StackExchange.Redis;

public sealed class RedisChatsService : IChatsService
{
    private const string TitleField = "title";

    private static readonly JsonSerializerOptions RedisJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ResponsesClient _responsesClient;
    private readonly IDatabase _database;
    private readonly string _model;
    private readonly int _maxOutputTokenCount;

    public RedisChatsService(
        ResponsesClient responsesClient,
        IDatabase database,
        IConfiguration configuration)
    {
        _responsesClient = responsesClient;
        _database = database;
        _model = configuration.GetValue<string?>("OpenAIModel") ?? throw new InvalidOperationException("Invalid 'OpenAIModel'.");
        _maxOutputTokenCount = configuration.GetValue<int?>("OpenAIMaxOutputTokenCount") ?? throw new InvalidOperationException("Invalid 'OpenAIMaxOutputTokenCount'.");
    }

    public async Task<IReadOnlyList<Chat>> GetChatsAsync(string email, CancellationToken cancellationToken = default)
    {
        var members = await _database.SortedSetRangeByRankAsync(ChatsKey(email), order: Order.Descending);
        var chats = new List<Chat>(members.Length);
        foreach (var member in members)
        {
            if (!Guid.TryParse(member.ToString(), out var chatId))
            {
                continue;
            }

            var meta = await _database.HashGetAllAsync(ChatMetaKey(chatId));
            var title = GetMetaField(meta, TitleField);
            var createdAt = long.TryParse(GetMetaField(meta, "createdAt"), out var ts) ? ts : 0L;
            chats.Add(new Chat(chatId, IsNullOrEmpty(title) ? null : title, createdAt));
        }

        return chats;
    }

    public async Task<Chat> GetChatAsync(string email, Guid chatId, CancellationToken cancellationToken = default)
    {
        await VerifyOwnershipAsync(email, chatId);
        var meta = await _database.HashGetAllAsync(ChatMetaKey(chatId));
        var title = GetMetaField(meta, TitleField);
        var createdAt = long.TryParse(GetMetaField(meta, "createdAt"), out var ts) ? ts : 0L;
        return new Chat(chatId, IsNullOrEmpty(title) ? null : title, createdAt);
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetChatMessagesAsync(string email, Guid chatId, CancellationToken cancellationToken = default)
    {
        await VerifyOwnershipAsync(email, chatId);
        return await GetChatMessagesInternalAsync(chatId);
    }

    public async Task<Chat> CreateChatAsync(string email, CancellationToken cancellationToken = default)
    {
        var chatId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        HashEntry[] meta = [new HashEntry(TitleField, string.Empty), new HashEntry("createdAt", createdAt)];
        await _database.HashSetAsync(ChatMetaKey(chatId), meta);
        await _database.SortedSetAddAsync(ChatsKey(email), chatId.ToString("N"), (double)createdAt);
        return new Chat(chatId, null, createdAt);
    }

    public async Task UpdateChatTitleAsync(string email, Guid chatId, string title, CancellationToken cancellationToken = default)
    {
        await VerifyOwnershipAsync(email, chatId);
        await _database.HashSetAsync(ChatMetaKey(chatId), TitleField, title);
    }

    public async Task DeleteChatAsync(string email, Guid chatId, CancellationToken cancellationToken = default)
    {
        var key = ChatsKey(email);
        var score = await _database.SortedSetScoreAsync(key, chatId.ToString("N"));
        if (score is null)
        {
            throw new KeyNotFoundException($"Chat '{chatId:N}' not found for user.");
        }

        await _database.SortedSetRemoveAsync(key, chatId.ToString("N"));
        await _database.KeyDeleteAsync([ChatMetaKey(chatId), ChatMessagesKey(chatId)]);
    }

    public async Task<(Guid ChatId, string? OutputText)> CompleteChatAsync(
        string email,
        Guid chatId,
        string inputText,
        CancellationToken cancellationToken = default)
    {
        if (IsNullOrWhiteSpace(inputText))
        {
            throw new ArgumentNullException(nameof(inputText));
        }

        await VerifyOwnershipAsync(email, chatId);

        var history = await GetChatMessagesInternalAsync(chatId);
        var inputItems = BuildInputItems(history, inputText);
        var options = new CreateResponseOptions(_model, inputItems)
        {
            MaxOutputTokenCount = _maxOutputTokenCount
        };

        var response = await _responsesClient.CreateResponseAsync(options, cancellationToken);
        var outputText = response?.Value?.GetOutputText() ?? throw new InvalidOperationException("OpenAI returned no output.");

        await StoreMessagesAsync(chatId, inputText, outputText);
        await _database.SortedSetAddAsync(ChatsKey(email), chatId.ToString("N"), Score());
        await SetAutoTitleIfNeededAsync(chatId, inputText);

        return (chatId, outputText);
    }

    public IAsyncEnumerable<string> StreamChatAsync(
        string email,
        Guid chatId,
        string inputText,
        CancellationToken cancellationToken = default)
    {
        if (IsNullOrWhiteSpace(inputText))
        {
            throw new ArgumentNullException(nameof(inputText));
        }

        return StreamChatAsyncCore(email, chatId, inputText, cancellationToken);
    }

    private static string ChatsKey(string email) => $"user:{email}:chats";

    private static string ChatMetaKey(Guid chatId) => $"chat:{chatId:N}:meta";

    private static string ChatMessagesKey(Guid chatId) => $"chat:{chatId:N}:messages";

    private static double Score() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string SerializeMessage(string role, string text) =>
        JsonSerializer.Serialize(new ChatHistoryMessage(role, text), RedisJsonOptions);

    private static string? GetMetaField(HashEntry[] entries, string field) =>
        entries.FirstOrDefault(e => e.Name == field).Value.ToString();

    private static ResponseItem[] BuildInputItems(IReadOnlyList<ChatHistoryMessage> history, string inputText)
    {
        var items = new List<ResponseItem>(history.Count + 1);
        foreach (var msg in history)
        {
            items.Add(msg.Role == "user"
                ? ResponseItem.CreateUserMessageItem(msg.Text)
                : ResponseItem.CreateAssistantMessageItem(msg.Text));
        }

        items.Add(ResponseItem.CreateUserMessageItem(inputText));
        return [.. items];
    }

    private async IAsyncEnumerable<string> StreamChatAsyncCore(
        string email,
        Guid chatId,
        string inputText,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await VerifyOwnershipAsync(email, chatId);

        var history = await GetChatMessagesInternalAsync(chatId);
        var inputItems = BuildInputItems(history, inputText);
        var options = new CreateResponseOptions(_model, inputItems)
        {
            MaxOutputTokenCount = _maxOutputTokenCount,
            StreamingEnabled = true
        };

        var accumulated = new StringBuilder();
        try
        {
            var updates = _responsesClient.CreateResponseStreamingAsync(options, cancellationToken);
            await foreach (var update in updates.OfType<StreamingResponseOutputTextDeltaUpdate>().WithCancellation(cancellationToken))
            {
                accumulated.Append(update.Delta);
                yield return update.Delta;
            }
        }
        finally
        {
            var assistantText = accumulated.ToString();
            if (!IsNullOrEmpty(assistantText))
            {
                await StoreMessagesAsync(chatId, inputText, assistantText);
                await _database.SortedSetAddAsync(ChatsKey(email), chatId.ToString("N"), Score());
                await SetAutoTitleIfNeededAsync(chatId, inputText);
            }
        }
    }

    private async Task<IReadOnlyList<ChatHistoryMessage>> GetChatMessagesInternalAsync(Guid chatId)
    {
        var items = await _database.ListRangeAsync(ChatMessagesKey(chatId));
        var messages = new List<ChatHistoryMessage>(items.Length);
        foreach (var item in items)
        {
            var msg = JsonSerializer.Deserialize<ChatHistoryMessage>(item.ToString(), RedisJsonOptions);
            if (msg is not null)
            {
                messages.Add(msg);
            }
        }

        return messages;
    }

    private async Task VerifyOwnershipAsync(string email, Guid chatId)
    {
        var score = await _database.SortedSetScoreAsync(ChatsKey(email), chatId.ToString("N"));
        if (score is null)
        {
            throw new KeyNotFoundException($"Chat '{chatId:N}' not found for user.");
        }
    }

    private async Task StoreMessagesAsync(Guid chatId, string userText, string assistantText)
    {
        var messagesKey = ChatMessagesKey(chatId);
        await _database.ListRightPushAsync(messagesKey, [SerializeMessage("user", userText), SerializeMessage("assistant", assistantText)]);
    }

    private async Task SetAutoTitleIfNeededAsync(Guid chatId, string inputText)
    {
        var existing = await _database.HashGetAsync(ChatMetaKey(chatId), TitleField);
        if (!existing.HasValue || IsNullOrEmpty(existing.ToString()))
        {
            var title = inputText.Length <= 60 ? inputText : inputText[..60] + "…";
            await _database.HashSetAsync(ChatMetaKey(chatId), TitleField, title);
        }
    }
}
