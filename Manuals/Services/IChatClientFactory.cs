using OpenAI.Chat;

namespace Manuals.Services;

public interface IChatClientFactory
{
    ChatClient CreateChatClient();
}
