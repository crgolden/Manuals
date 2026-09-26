namespace Manuals.Services;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Manuals.Extensions;
using Manuals.Models;
using Microsoft.Extensions.Caching.Hybrid;
using OpenAI.Responses;
using StackExchange.Redis;

public sealed class RedisChatsService : IChatsService
{
    internal const string TitleField = "title";

    internal const string CreatedAtField = "createdAt";

    internal const string UserRole = "user";

    internal const string AssistantRole = "assistant";

    internal const string ModelConfigurationKey = "OpenAIModel";

    internal const string MaxOutputTokenCountConfigurationKey = "OpenAIMaxOutputTokenCount";

    internal const string InstructionsConfigurationKey = "OpenAIInstructions";

    internal const string CacheInstanceName = "manuals:hc:";

    internal const int AutoTitleMaxLength = 60;

    private const string AutoTitleEllipsis = "…";

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
    private readonly Telemetry _telemetry;

    public RedisChatsService(
        ResponsesClient responsesClient,
        IDatabase database,
        HybridCache cache,
        IConfiguration configuration,
        Telemetry telemetry)
    {
        _responsesClient = responsesClient;
        _database = database;
        _cache = cache;
        _telemetry = telemetry;
        _model = configuration.GetRequired<string>(ModelConfigurationKey);
        _maxOutputTokenCount = configuration.GetRequired<int>(MaxOutputTokenCountConfigurationKey);
        _instructions = configuration.GetRequired<string>(InstructionsConfigurationKey);
    }

    public async Task<IReadOnlyList<Chat>> GetChatsAsync(string userId, CancellationToken cancellationToken = default) =>
        await _cache.GetOrCreateAsync(
            ChatListCacheKey(userId),
            async _ =>
            {
                using var activity = _telemetry.StartActivity("manuals.chat.list");
                activity?.SetTag(Telemetry.UserIdTag, userId);
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
                    var createdAt = long.TryParse(GetMetaField(meta, CreatedAtField), out var ts) ? ts : 0L;
                    chats.Add(new Chat(chatId, IsNullOrWhiteSpace(title) ? null : title, createdAt));
                }

                activity?.SetTag(Telemetry.ChatCountTag, chats.Count);
                return (IReadOnlyList<Chat>)chats;
            },
            ChatListCacheOptions,
            cancellationToken: cancellationToken);

    public async Task<Chat> GetChatAsync(string userId, Guid chatId, CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("manuals.chat.get");
        activity?.SetTag(Telemetry.ChatIdTag, chatId);
        await VerifyOwnershipAsync(userId, chatId);
        var meta = await _database.HashGetAllAsync(ChatMetaKey(chatId));
        var title = GetMetaField(meta, TitleField);
        var createdAt = long.TryParse(GetMetaField(meta, CreatedAtField), out var ts) ? ts : 0L;
        return new Chat(chatId, IsNullOrWhiteSpace(title) ? null : title, createdAt);
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetChatMessagesAsync(string userId, Guid chatId, CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("manuals.chat.get_messages");
        activity?.SetTag(Telemetry.ChatIdTag, chatId);
        await VerifyOwnershipAsync(userId, chatId);
        var messages = await GetChatMessagesInternalAsync(chatId);
        activity?.SetTag(Telemetry.MessageCountTag, messages.Count);
        return messages;
    }

    public async Task<Chat> CreateChatAsync(string userId, CancellationToken cancellationToken = default)
    {
        var chatId = Guid.NewGuid();
        using var activity = _telemetry.StartActivity("manuals.chat.create");
        activity?.SetTag(Telemetry.ChatIdTag, chatId);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        HashEntry[] meta = [new HashEntry(CreatedAtField, createdAt)];
        await _database.HashSetAsync(ChatMetaKey(chatId), meta);
        await _database.SortedSetAddAsync(ChatsKey(userId), ChatMember(chatId), createdAt);
        await _cache.RemoveAsync(ChatListCacheKey(userId), cancellationToken);
        return new Chat(chatId, null, createdAt);
    }

    public async Task UpdateChatTitleAsync(string userId, Guid chatId, string title, CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("manuals.chat.update_title");
        activity?.SetTag(Telemetry.ChatIdTag, chatId);
        await VerifyOwnershipAsync(userId, chatId);
        await _database.HashSetAsync(ChatMetaKey(chatId), TitleField, title);
        await _cache.RemoveAsync(ChatListCacheKey(userId), cancellationToken);
    }

    public async Task DeleteChatAsync(string userId, Guid chatId, CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("manuals.chat.delete");
        activity?.SetTag(Telemetry.ChatIdTag, chatId);
        var key = ChatsKey(userId);
        var score = await _database.SortedSetScoreAsync(key, ChatMember(chatId));
        if (score is null)
        {
            throw new KeyNotFoundException($"Chat '{chatId:N}' not found for user.");
        }

        await _database.SortedSetRemoveAsync(key, ChatMember(chatId));
        await _database.KeyDeleteAsync([ChatMetaKey(chatId), ChatMessagesKey(chatId)]);
        await _cache.RemoveAsync(ChatListCacheKey(userId), cancellationToken);
        await _cache.RemoveAsync(ChatMessagesCacheKey(chatId), cancellationToken);
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
            Instructions = _instructions
        };

        using var activity = _telemetry.StartActivity("manuals.openai.complete_chat");
        activity?.SetTag(Telemetry.AiModelTag, _model);

        var response = await _responsesClient.CreateResponseAsync(options, cancellationToken);
        var outputText = response?.Value?.GetOutputText() ?? throw new InvalidOperationException("OpenAI returned no output.");

        await StoreMessagesAsync(chatId, inputText, outputText);
        await _database.SortedSetAddAsync(ChatsKey(userId), ChatMember(chatId), Score());
        await SetAutoTitleIfNeededAsync(chatId, inputText);
        await _cache.RemoveAsync(ChatListCacheKey(userId), cancellationToken);

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

    internal static string ChatsKey(string userId) => $"user:{userId}:chats";

    internal static string ChatMetaKey(Guid chatId) => $"chat:{chatId:N}:meta";

    internal static string ChatMessagesKey(Guid chatId) => $"chat:{chatId:N}:messages";

    internal static string ChatMember(Guid chatId) => chatId.ToString("N");

    internal static string ChatListCacheKey(string userId) => $"chats:{userId}";

    internal static string ChatMessagesCacheKey(Guid chatId) => $"messages:{chatId:N}";

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
            items.Add(string.Equals(msg.Role, UserRole, StringComparison.Ordinal)
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
            StreamingEnabled = true
        };

        using var activity = _telemetry.StartActivity("manuals.openai.stream_chat");
        activity?.SetTag(Telemetry.AiModelTag, _model);

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
            if (!IsNullOrWhiteSpace(assistantText))
            {
                await StoreMessagesAsync(chatId, inputText, assistantText);
                await _database.SortedSetAddAsync(ChatsKey(userId), ChatMember(chatId), Score());
                await SetAutoTitleIfNeededAsync(chatId, inputText);
                await _cache.RemoveAsync(ChatListCacheKey(userId), CancellationToken.None);
            }
        }
    }

    private ValueTask<IReadOnlyList<ChatHistoryMessage>> GetChatMessagesInternalAsync(Guid chatId) =>
        _cache.GetOrCreateAsync(
            ChatMessagesCacheKey(chatId),
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
        using var activity = _telemetry.StartActivity("manuals.chat.verify_ownership");
        activity?.SetTag(Telemetry.ChatIdTag, chatId);
        activity?.SetTag(Telemetry.UserIdTag, userId);
        var score = await _database.SortedSetScoreAsync(ChatsKey(userId), ChatMember(chatId));
        activity?.SetTag(Telemetry.VerifiedTag, score.HasValue);
        if (score is null)
        {
            throw new KeyNotFoundException($"Chat '{chatId:N}' not found for user.");
        }
    }

    private async Task StoreMessagesAsync(Guid chatId, string userText, string assistantText)
    {
        var messagesKey = ChatMessagesKey(chatId);
        await _database.ListRightPushAsync(messagesKey, [SerializeMessage(UserRole, userText), SerializeMessage(AssistantRole, assistantText)]);
        await _cache.RemoveAsync(ChatMessagesCacheKey(chatId));
    }

    private async Task SetAutoTitleIfNeededAsync(Guid chatId, string inputText)
    {
        var existing = await _database.HashGetAsync(ChatMetaKey(chatId), TitleField);
        if (!existing.HasValue || IsNullOrWhiteSpace(existing.ToString()))
        {
            var title = inputText.Length <= AutoTitleMaxLength
                ? inputText
                : inputText[..AutoTitleMaxLength] + AutoTitleEllipsis;
            await _database.HashSetAsync(ChatMetaKey(chatId), TitleField, title);
        }
    }
}
