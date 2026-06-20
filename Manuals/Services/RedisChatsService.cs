#pragma warning disable OPENAI001
namespace Manuals.Services;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Extensions;
using Microsoft.Extensions.Caching.Hybrid;
using Models;
using OpenAI.Responses;
using StackExchange.Redis;

public sealed class RedisChatsService : IChatsService
{
    private const string TitleField = "title";

    private static readonly JsonSerializerOptions RedisJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HybridCacheEntryOptions MessagesCacheOptions = new()
    {
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
        Expiration = TimeSpan.FromMinutes(30)
    };

    private static readonly HybridCacheEntryOptions ChatListCacheOptions = new()
    {
        LocalCacheExpiration = TimeSpan.FromMinutes(1),
        Expiration = TimeSpan.FromMinutes(5)
    };

    private readonly ResponsesClient _responsesClient;
    private readonly IDatabase _database;
    private readonly HybridCache _cache;
    private readonly string _model;
    private readonly int _maxOutputTokenCount;
    private readonly string _instructions;

    public RedisChatsService(
        ResponsesClient responsesClient,
        IDatabase database,
        HybridCache cache,
        IConfiguration configuration)
    {
        _responsesClient = responsesClient;
        _database = database;
        _cache = cache;
        _model = configuration.GetRequired<string>("OpenAIModel");
        _maxOutputTokenCount = configuration.GetRequired<int>("OpenAIMaxOutputTokenCount");
        _instructions = configuration.GetRequired<string>("OpenAIInstructions");
    }

    public async Task<IReadOnlyList<Chat>> GetChatsAsync(string userId, CancellationToken cancellationToken = default) =>
        await _cache.GetOrCreateAsync(
            $"chats:{userId}",
            async _ =>
            {
                using var activity = Telemetry.StartActivity("manuals.chat.list");
                activity?.SetTag("user.id", userId);
                var members = await _database.SortedSetRangeByRankAsync(ChatsKey(userId), order: Order.Descending);
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

                activity?.SetTag("chat_count", chats.Count);
                return (IReadOnlyList<Chat>)chats;
            },
            ChatListCacheOptions,
            cancellationToken: cancellationToken);

    public async Task<Chat> GetChatAsync(string userId, Guid chatId, CancellationToken cancellationToken = default)
    {
        using var activity = Telemetry.StartActivity("manuals.chat.get");
        activity?.SetTag("chat.id", chatId);
        await VerifyOwnershipAsync(userId, chatId);
        var meta = await _database.HashGetAllAsync(ChatMetaKey(chatId));
        var title = GetMetaField(meta, TitleField);
        var createdAt = long.TryParse(GetMetaField(meta, "createdAt"), out var ts) ? ts : 0L;
        return new Chat(chatId, IsNullOrEmpty(title) ? null : title, createdAt);
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetChatMessagesAsync(string userId, Guid chatId, CancellationToken cancellationToken = default)
    {
        using var activity = Telemetry.StartActivity("manuals.chat.get_messages");
        activity?.SetTag("chat.id", chatId);
        await VerifyOwnershipAsync(userId, chatId);
        var messages = await GetChatMessagesInternalAsync(chatId);
        activity?.SetTag("message_count", messages.Count);
        return messages;
    }

    public async Task<Chat> CreateChatAsync(string userId, CancellationToken cancellationToken = default)
    {
        var chatId = Guid.NewGuid();
        using var activity = Telemetry.StartActivity("manuals.chat.create");
        activity?.SetTag("chat.id", chatId);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        HashEntry[] meta = [new HashEntry(TitleField, Empty), new HashEntry("createdAt", createdAt)];
        await _database.HashSetAsync(ChatMetaKey(chatId), meta);
        await _database.SortedSetAddAsync(ChatsKey(userId), chatId.ToString("N"), createdAt);
        await _cache.RemoveAsync($"chats:{userId}", cancellationToken);
        return new Chat(chatId, null, createdAt);
    }

    public async Task UpdateChatTitleAsync(string userId, Guid chatId, string title, CancellationToken cancellationToken = default)
    {
        using var activity = Telemetry.StartActivity("manuals.chat.update_title");
        activity?.SetTag("chat.id", chatId);
        await VerifyOwnershipAsync(userId, chatId);
        await _database.HashSetAsync(ChatMetaKey(chatId), TitleField, title);
        await _cache.RemoveAsync($"chats:{userId}", cancellationToken);
    }

    public async Task DeleteChatAsync(string userId, Guid chatId, CancellationToken cancellationToken = default)
    {
        using var activity = Telemetry.StartActivity("manuals.chat.delete");
        activity?.SetTag("chat.id", chatId);
        var key = ChatsKey(userId);
        var score = await _database.SortedSetScoreAsync(key, chatId.ToString("N"));
        if (score is null)
        {
            throw new KeyNotFoundException($"Chat '{chatId:N}' not found for user.");
        }

        await _database.SortedSetRemoveAsync(key, chatId.ToString("N"));
        await _database.KeyDeleteAsync([ChatMetaKey(chatId), ChatMessagesKey(chatId)]);
        await _cache.RemoveAsync($"chats:{userId}", cancellationToken);
        await _cache.RemoveAsync($"messages:{chatId:N}", cancellationToken);
    }

    public async Task<(Guid ChatId, string? OutputText)> CompleteChatAsync(
        string userId,
        Guid chatId,
        string inputText,
        CancellationToken cancellationToken = default)
    {
        if (IsNullOrWhiteSpace(inputText))
        {
            throw new ArgumentNullException(nameof(inputText));
        }

        await VerifyOwnershipAsync(userId, chatId);

        var history = await GetChatMessagesInternalAsync(chatId);
        var inputItems = BuildInputItems(history, inputText);
        var options = new CreateResponseOptions(_model, inputItems)
        {
            MaxOutputTokenCount = _maxOutputTokenCount,
            Instructions = _instructions,
            Tools = { ResponseTool.CreateWebSearchTool() }
        };

        using var activity = Telemetry.StartActivity("manuals.openai.complete_chat");
        activity?.SetTag("ai.model", _model);

        var response = await _responsesClient.CreateResponseAsync(options, cancellationToken);
        var outputText = response?.Value?.GetOutputText() ?? throw new InvalidOperationException("OpenAI returned no output.");

        await StoreMessagesAsync(chatId, inputText, outputText);
        await _database.SortedSetAddAsync(ChatsKey(userId), chatId.ToString("N"), Score());
        await SetAutoTitleIfNeededAsync(chatId, inputText);
        await _cache.RemoveAsync($"chats:{userId}", cancellationToken);

        return (chatId, outputText);
    }

    public IAsyncEnumerable<string> StreamChatAsync(
        string userId,
        Guid chatId,
        string inputText,
        CancellationToken cancellationToken = default)
    {
        if (IsNullOrWhiteSpace(inputText))
        {
            throw new ArgumentNullException(nameof(inputText));
        }

        return StreamChatAsyncCore(userId, chatId, inputText, cancellationToken);
    }

    private static string ChatsKey(string userId) => $"user:{userId}:chats";

    private static string ChatMetaKey(Guid chatId) => $"chat:{chatId:N}:meta";

    private static string ChatMessagesKey(Guid chatId) => $"chat:{chatId:N}:messages";

    private static double Score() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string SerializeMessage(string role, string text) =>
        JsonSerializer.Serialize(new ChatHistoryMessage(role, text), RedisJsonOptions);

    private static string GetMetaField(HashEntry[] entries, string field) =>
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
        string userId,
        Guid chatId,
        string inputText,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await VerifyOwnershipAsync(userId, chatId);

        var history = await GetChatMessagesInternalAsync(chatId);
        var inputItems = BuildInputItems(history, inputText);
        var options = new CreateResponseOptions(_model, inputItems)
        {
            MaxOutputTokenCount = _maxOutputTokenCount,
            Instructions = _instructions,
            StreamingEnabled = true,
            Tools = { ResponseTool.CreateWebSearchTool() }
        };

        using var activity = Telemetry.StartActivity("manuals.openai.stream_chat");
        activity?.SetTag("ai.model", _model);

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
                await _database.SortedSetAddAsync(ChatsKey(userId), chatId.ToString("N"), Score());
                await SetAutoTitleIfNeededAsync(chatId, inputText);
                await _cache.RemoveAsync($"chats:{userId}", CancellationToken.None);
            }
        }
    }

    private ValueTask<IReadOnlyList<ChatHistoryMessage>> GetChatMessagesInternalAsync(Guid chatId) =>
        _cache.GetOrCreateAsync(
            $"messages:{chatId:N}",
            async _ =>
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

                return (IReadOnlyList<ChatHistoryMessage>)messages;
            },
            MessagesCacheOptions);

    private async Task VerifyOwnershipAsync(string userId, Guid chatId)
    {
        using var activity = Telemetry.StartActivity("manuals.chat.verify_ownership");
        activity?.SetTag("chat.id", chatId);
        activity?.SetTag("user.id", userId);
        var score = await _database.SortedSetScoreAsync(ChatsKey(userId), chatId.ToString("N"));
        activity?.SetTag("verified", score.HasValue);
        if (score is null)
        {
            throw new KeyNotFoundException($"Chat '{chatId:N}' not found for user.");
        }
    }

    private async Task StoreMessagesAsync(Guid chatId, string userText, string assistantText)
    {
        var messagesKey = ChatMessagesKey(chatId);
        await _database.ListRightPushAsync(messagesKey, [SerializeMessage("user", userText), SerializeMessage("assistant", assistantText)]);
        await _cache.RemoveAsync($"messages:{chatId:N}");
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
