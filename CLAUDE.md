# Manuals — Project Memory

## Architecture

We follow the **onion / clean architecture** model:

- **Entries (Controllers/Hubs)** depend on **Business Logic (Services)**
- **Services** depend on **Persistence (Data Layer)**

This layering keeps the codebase clean, reusable, and testable, and eliminates any possibility of circular dependencies. Never allow a lower layer to reference a higher one.

## API Endpoints

All endpoints require a valid JWT Bearer token with the `manuals` scope (enforced by the `nameof(Manuals)` authorization policy).

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/chat` | Single-turn response. Body: `{ input, conversationId? }`. Returns `{ output, conversationId }`. |
| `POST` | `/api/chat/stream` | Streaming response via Server-Sent Events. Same body as above. Returns `text/event-stream` with `data: {"delta":{"content":"..."}}` lines, terminated by `data: [DONE]`. |
| `GET` | `/api/chat/conversations` | Returns all conversation IDs for the authenticated user (`string[]`), keyed by `email` claim in Redis. |
| `GET` | `/api/chat/conversations/{conversationId}` | Returns `ConversationDetails { conversationId, createdAt }` for one conversation. 404 if not owned by the user. |
| `GET` | `/api/chat/conversations/{conversationId}/items` | Returns `ConversationItemSummary[]` (the message history). Each item: `{ id, role, text }`. 404 if not owned by the user. |
| `POST` | `/api/chat/conversations` | Creates a new conversation. Returns `201 Created` with `{ conversationId }` and a `Location` header pointing to `GET /api/chat/conversations/{conversationId}`. |
| `DELETE` | `/api/chat/conversations/{conversationId}` | Deletes a conversation. Returns 204 No Content. 404 if not owned by the user. |

### Conversation lifecycle

Callers should:
1. `GET /api/chat/conversations` to load all existing conversation IDs for the user.
2. `GET /api/chat/conversations/{id}/items` to restore message history when resuming a conversation.
3. `POST /api/chat/conversations` to create a new conversation and obtain a `conversationId`.
4. Pass `conversationId` in subsequent `POST /api/chat` or `POST /api/chat/stream` requests to maintain context.
5. `DELETE /api/chat/conversations/{conversationId}` when explicitly discarding a conversation.

Conversation IDs are scoped per user by the `email` JWT claim, stored in Redis under the key `user:{email}:conversations`.

### Streaming implementation note

`POST /api/chat/stream` calls `IChatService.StreamChatAsync()`, which returns `IAsyncEnumerable<string>`. The controller iterates the async enumerable and writes each delta as a `data: {...}\n\n` SSE line, then writes `data: [DONE]\n\n`. The action calls `IHttpResponseBodyFeature.DisableBuffering()` first so that YARP and ASP.NET Core do not buffer the response and deltas are flushed immediately to the caller.

### Model changes

`ConversationId` replaced the earlier `PreviousResponseId` field on `ChatRequest` and `ChatResponse`. Do not use `PreviousResponseId` — it no longer exists.
