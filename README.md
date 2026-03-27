# Manuals

ASP.NET Core 10 API that proxies requests to Azure OpenAI, with both a REST endpoint for standard completions and a SignalR hub for real-time streaming.

## Architecture

```
Client
  ├── POST /api/chat          → ChatController → AzureOpenAIChatService → AzureOpenAIClient → Azure OpenAI
  ├── DELETE /api/chat/{id}   → ChatController → IConversationHistoryStore
  └── /hubs/chat (SignalR)    → ChatHub → AzureOpenAIChatService (streaming) → AzureOpenAIClient → Azure OpenAI

AzureOpenAIChatService ←→ InMemoryConversationHistoryStore  (per-conversation message history)
AzureOpenAIChatClientFactory  (constructs AzureOpenAIClient with DefaultAzureCredential)
```

Authentication uses `DefaultAzureCredential` — Managed Identity in Azure, `az login` locally. No secrets are stored in code or config.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) (`az login` for local dev)
- Azure OpenAI resource with a deployed model (e.g., `gpt-4o`)

## Local Setup

```bash
# 1. Authenticate with Azure
az login

# 2. Store the OpenAI endpoint in user secrets (never commit this)
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://<your-resource>.openai.azure.com" \
  --project Manuals/Manuals.csproj

# 3. (Optional) Override the deployment name
dotnet user-secrets set "AzureOpenAI:DeploymentName" "gpt-4o" \
  --project Manuals/Manuals.csproj

# 4. Run
dotnet run --project Manuals/Manuals.csproj
```

The app starts at `https://localhost:7099` / `http://localhost:5053`.

- OpenAPI spec: `GET /openapi/v1.json` (development only)
- SignalR test page: `http://localhost:5053/chat-test.html`

## REST Endpoint Reference

### `POST /api/chat`

Send a message and receive a complete response.

**Request body:**
```json
{
  "conversationId": "string",   // required — unique ID for this conversation thread
  "message": "string",          // required — the user's message
  "systemPrompt": "string"      // optional — applied only on the first turn of a conversation
}
```

**Response `200 OK`:**
```json
{
  "conversationId": "string",
  "message": "string",
  "finishReason": "stop"
}
```

**Response `400 Bad Request`** — when `conversationId` or `message` is empty.

---

### `DELETE /api/chat/{conversationId}`

Clears the stored message history for the given conversation. The next message to this `conversationId` will start a fresh context. Returns `204 No Content`.

## SignalR Hub Reference

**Endpoint:** `/hubs/chat`

### JavaScript client example

```javascript
import * as signalR from "@microsoft/signalr";

const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/chat")
  .withAutomaticReconnect()
  .build();

connection.on("ReceiveSignal", (signal) => {
  switch (signal.type) {
    case "typing":
      // AI is processing — show a spinner
      break;
    case "partial":
      // Append signal.content to the displayed response
      break;
    case "completed":
      // signal.content contains the full assembled response
      break;
  }
});

await connection.start();

// Invoke a streaming completion
await connection.invoke("SendMessage", {
  conversationId: "conv-001",
  message: "Explain quantum entanglement simply.",
  systemPrompt: "You are a concise science communicator."  // optional
});
```

### Signal types

| `type`      | Fields                              | Description                              |
|-------------|-------------------------------------|------------------------------------------|
| `typing`    | `conversationId`                    | AI has started processing                |
| `partial`   | `conversationId`, `content`         | Incremental text chunk (buffered ≥20 chars) |
| `completed` | `conversationId`, `content`         | Full assembled response; streaming done  |

## Running Tests

```bash
dotnet test Manuals.slnx
```

Integration tests use `WebApplicationFactory<Program>` with in-memory config and a mocked `IChatService` — no real Azure calls are made during CI.

## Deployment

### Azure Resources

| Resource | Notes |
|---|---|
| Azure OpenAI | Requires a deployed model (e.g. `gpt-4o`) |
| Azure App Service (Linux, .NET 10) | Enable **Web sockets** under General Settings |
| Managed Identity (system-assigned) | Assign **Cognitive Services OpenAI User** role on the OpenAI resource |
| App Registration (for GitHub Actions) | Add federated credential for `repo:<org>/Manuals:ref:refs/heads/main` |

### App Service Application Settings

| Key | Value |
|---|---|
| `AzureOpenAI__Endpoint` | `https://<your-resource>.openai.azure.com` |
| `AzureOpenAI__DeploymentName` | `gpt-4o` |

### GitHub Actions Secrets

| Secret | Description |
|---|---|
| `AZURE_CLIENT_ID` | App Registration client ID |
| `AZURE_TENANT_ID` | Azure AD tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |
| `AZURE_WEBAPP_NAME` | App Service name |

Push to `main` triggers build → test → publish → deploy. Pull requests run build and test only.

### Scaling Considerations

- `InMemoryConversationHistoryStore` is process-local. For multi-instance deployments, replace it with a Redis-backed implementation (the `IConversationHistoryStore` interface is designed for this swap).
- Add `AddAzureSignalR()` with the Azure SignalR Service connection string to coordinate hub connections across multiple App Service instances.
