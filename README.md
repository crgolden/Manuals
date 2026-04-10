# Manuals

[![Build and deploy ASP.Net Core app to Azure Web App - crgolden-manuals](https://github.com/crgolden/Manuals/actions/workflows/main_crgolden-manuals.yml/badge.svg)](https://github.com/crgolden/Manuals/actions/workflows/main_crgolden-manuals.yml)

[![Quality Gate Status](https://sonarcloud.io/api/project_badges/measure?project=crgolden_Manuals&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=crgolden_Manuals)

ASP.NET Core 10 API that proxies requests to Azure OpenAI. Provides REST endpoints for managing AI chat sessions with full message history stored in Redis.

## Architecture

```
Client
  ├── GET/POST/PATCH/DELETE /api/chats     → ChatsController → RedisChatsService → Redis
  ├── GET /api/chats/{id}/messages         → ChatsController → RedisChatsService → Redis
  ├── POST /api/chats/{id}/messages        → ChatsController → RedisChatsService → ResponsesClient → Azure OpenAI
  └── POST /api/chats/{id}/messages/stream → ChatsController → RedisChatsService → ResponsesClient (SSE) → Azure OpenAI
```

Authentication uses `DefaultAzureCredential` — Managed Identity in Azure, `az login` locally. No secrets are stored in code or config.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) (`az login` for local dev)
- Azure OpenAI resource with a deployed model (e.g., `gpt-4o`)
- Redis instance (Azure Cache for Redis or local)

## Local Setup

```bash
# 1. Authenticate with Azure
az login

# 2. Store secrets in user secrets
dotnet user-secrets set "OpenAIEndpoint" "https://<your-resource>.openai.azure.com/" --project Manuals/Manuals.csproj
dotnet user-secrets set "OpenAIModel" "gpt-4o" --project Manuals/Manuals.csproj
dotnet user-secrets set "OpenAIMaxOutputTokenCount" "4096" --project Manuals/Manuals.csproj
dotnet user-secrets set "RedisHost" "<your-redis-host>" --project Manuals/Manuals.csproj
dotnet user-secrets set "RedisPort" "6380" --project Manuals/Manuals.csproj

# 3. Run
dotnet run --project Manuals/Manuals.csproj
```

The app starts at `https://localhost:7099`.

- OpenAPI spec: `GET /openapi/v1.json` (development only)

## REST Endpoint Reference

All endpoints require a valid JWT Bearer token with the `manuals` scope.

### `GET /api/chats`

Returns all chats for the authenticated user, ordered newest first.

**Response `200 OK`:**
```json
[
  { "chatId": "string", "title": "string | null", "createdAt": 1700000000000 }
]
```

---

### `GET /api/chats/{chatId}`

Returns a single chat by ID.

**Response `200 OK`:** `{ "chatId": "string", "title": "string | null", "createdAt": number }`

**Response `404 Not Found`** — when the chat does not exist or is not owned by the user.

---

### `GET /api/chats/{chatId}/messages`

Returns the full message history for a chat.

**Response `200 OK`:**
```json
[
  { "role": "user", "text": "Hello" },
  { "role": "assistant", "text": "Hi there!" }
]
```

---

### `POST /api/chats`

Creates a new chat.

**Response `201 Created`:** `{ "chatId": "string", "title": null, "createdAt": number }` with `Location` header.

---

### `PATCH /api/chats/{chatId}`

Updates the chat title.

**Request body** (`Content-Type: application/merge-patch+json`):
```json
{ "title": "My Chat Title" }
```

**Response `204 No Content`**

**Response `400 Bad Request`** — when title is null or whitespace.

**Response `404 Not Found`** — when the chat does not exist or is not owned by the user.

---

### `DELETE /api/chats/{chatId}`

Deletes a chat and all its stored messages from Redis.

**Response `204 No Content`**

**Response `404 Not Found`** — when the chat does not exist or is not owned by the user.

---

### `POST /api/chats/{chatId}/messages`

Send a message and receive a complete response.

**Request body:**
```json
{ "input": "string" }
```

**Response `200 OK`:**
```json
{ "output": "string", "chatId": "string" }
```

**Response `400 Bad Request`** — when input is empty.

**Response `404 Not Found`** — when the chat does not exist or is not owned by the user.

On first message, the chat title is auto-set to the first 60 characters of input.

---

### `POST /api/chats/{chatId}/messages/stream`

Send a message and receive a streaming response via Server-Sent Events.

**Request body:** `{ "input": "string" }`

**Response** (`text/event-stream`):
```
data: {"delta":{"content":"Hello"}}

data: {"delta":{"content":" world"}}

data: [DONE]

```

## Known SDK Caveat

`RedisChatsService` uses `ResponseItem.CreateAssistantMessageItem(string text)` to reconstruct assistant turns when sending full conversation history to the Responses API. This experimental factory method (matching the pattern of `ResponseItem.CreateUserMessageItem`) **may not be present in OpenAI SDK v2.10.0**.

If the project fails to compile with a missing-method error, see the workaround in [`CLAUDE.md` → SDK caveat](CLAUDE.md#sdk-caveat--responseitemcreateassistantmessageitem).

## Testing

See [TESTING.md](TESTING.md) for the full testing guide — unit tests, nightly real-service tests, local prerequisites, and CI pipeline details.

## Deployment

### Azure Resources

| Resource | Notes |
|---|---|
| Azure OpenAI | Requires a deployed model (e.g. `gpt-4o`) |
| Azure Cache for Redis | SSL enabled, port 6380 |
| Azure App Service (Linux, .NET 10) | Standard or higher plan |
| Managed Identity (system-assigned) | Assign **Cognitive Services OpenAI User** role on the OpenAI resource |
| App Registration (for GitHub Actions) | Add federated credential for `repo:<org>/Manuals:ref:refs/heads/main` |

### App Service Application Settings

| Key | Value |
|---|---|
| `OpenAIEndpoint` | `https://<your-resource>.openai.azure.com/` |
| `OpenAIModel` | `gpt-4o` |
| `OpenAIMaxOutputTokenCount` | `4096` |
| `RedisHost` | `<your-redis>.redis.cache.windows.net` |
| `RedisPort` | `6380` |

### GitHub Actions Secrets

| Secret | Description |
|---|---|
| `AZURE_CLIENT_ID` | App Registration client ID |
| `AZURE_TENANT_ID` | Azure AD tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |
| `AZURE_WEBAPP_NAME` | App Service name |

Push to `main` triggers build → test → publish → deploy. Pull requests run build and test only.
