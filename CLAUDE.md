# Manuals — Project Memory

## Architecture

We follow the **onion / clean architecture** model:

- **Entries (Controllers)** depend on **Business Logic (Services)**
- **Services** depend on **Persistence (Redis) and AI (OpenAI ResponsesClient)**

This layering keeps the codebase clean, reusable, and testable, and eliminates any possibility of circular dependencies. Never allow a lower layer to reference a higher one.

## API Endpoints

All endpoints require a valid JWT Bearer token with the `manuals` scope (enforced by the `nameof(Manuals)` authorization policy).

### Chat CRUD (`/api/chats`)

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/chats` | Returns all chats for the authenticated user (`Chat[]`), ordered newest first. |
| `GET` | `/api/chats/{chatId}` | Returns `Chat { chatId, title, createdAt }` for one chat. 404 if not owned by the user. |
| `GET` | `/api/chats/{chatId}/messages` | Returns `ChatHistoryMessage[]` (full message history). 404 if not owned by the user. |
| `POST` | `/api/chats` | Creates a new chat. Returns `201 Created` with the `Chat` object and a `Location` header pointing to `GET /api/chats/{chatId}`. |
| `PATCH` | `/api/chats/{chatId}` | Updates the chat title. Body: `{ "title": "..." }` with `Content-Type: application/merge-patch+json`. Returns 204 No Content. |
| `DELETE` | `/api/chats/{chatId}` | Deletes a chat and all its stored messages. Returns 204 No Content. 404 if not owned by the user. |

### Messaging

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/chats/{chatId}/messages` | Single-turn completion. Body: `{ input }`. Returns `{ output, chatId }`. |
| `POST` | `/api/chats/{chatId}/messages/stream` | Streaming response via Server-Sent Events. Same body. Returns `text/event-stream` with `data: {"delta":{"content":"..."}}` lines, terminated by `data: [DONE]`. |

### Chat lifecycle

Callers should:
1. `GET /api/chats` to load all existing chats for the user (ordered newest first).
2. `GET /api/chats/{chatId}/messages` to restore message history when resuming a chat.
3. `POST /api/chats` to create a new chat and obtain a `chatId`.
4. `POST /api/chats/{chatId}/messages` or `.../stream` to send messages within that chat.
5. `PATCH /api/chats/{chatId}` to update the chat title.
6. `DELETE /api/chats/{chatId}` when explicitly discarding a chat.

Chat IDs are scoped per user by the `email` JWT claim. Redis enforces user isolation at the service layer.

### Auto-title behavior

When the first message is sent to a chat (via POST or stream), the chat title is automatically set to the first 60 characters of the user's input (with "…" suffix if longer). The title is stored in Redis and returned on subsequent `GET /api/chats` calls.

### Streaming implementation note

`POST /api/chats/{chatId}/messages/stream` calls `IChatsService.StreamChatAsync()`, which returns `IAsyncEnumerable<string>`. The controller iterates the async enumerable and writes each delta as a `data: {...}\n\n` SSE line, then writes `data: [DONE]\n\n`. The action calls `IHttpResponseBodyFeature.DisableBuffering()` first so that YARP and ASP.NET Core do not buffer the response and deltas are flushed immediately to the caller.

After streaming completes, the full accumulated response and the user message are stored in Redis.

## Redis Data Model

All chat data is stored in Redis. User isolation is enforced by the service layer (ownership checks via sorted set score).

| Key | Type | Contents |
|-----|------|----------|
| `user:{email}:chats` | Sorted Set | chatId members, score = Unix timestamp ms (newest = highest score) |
| `chat:{chatId}:meta` | Hash | fields: `title` (string, may be empty), `createdAt` (ms timestamp) |
| `chat:{chatId}:messages` | List | JSON strings appended chronologically: `{"role":"user","text":"..."}` |

Messages are stored chronologically (RPUSH). The chat list sorted set retrieves newest-first (`Order.Descending`).

## OpenAI Integration

- `ResponsesClient` (from `OpenAI.Responses`, marked OPENAI001 experimental) handles all completions and streaming.
- Full conversation history is loaded from Redis and passed as input items on every request — the app does not rely on OpenAI's server-side conversation storage.
- `ConversationClient` has been removed; there is no dependency on OpenAI's conversation management API.

### SDK caveat — `ResponseItem.CreateAssistantMessageItem`

`RedisChatsService` calls `ResponseItem.CreateAssistantMessageItem(string text)` to reconstruct assistant turns when building the history payload for each request. This factory method follows the same experimental pattern as `ResponseItem.CreateUserMessageItem`, but **it may not be present in OpenAI SDK v2.10.0**. If the project fails to compile with a "does not contain a definition" error on that call, replace it with a raw JSON approach:

```csharp
private static ResponseItem CreateAssistantItem(string text)
{
    var json = JsonSerializer.Serialize(new
    {
        type = "message",
        role = "assistant",
        content = new[] { new { type = "output_text", text } }
    });
    return ModelReaderWriter.Read<ResponseItem>(BinaryData.FromString(json), ModelReaderWriterOptions.Json)!;
}
```

Then swap the inline call in `BuildInputItems` from `ResponseItem.CreateAssistantMessageItem(msg.Text)` to `CreateAssistantItem(msg.Text)`. Requires `using Azure.Core.Serialization;` (already transitively available via `Azure.Security.KeyVault.Secrets`).
