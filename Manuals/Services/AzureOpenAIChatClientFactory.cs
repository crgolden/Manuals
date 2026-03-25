using Azure.AI.OpenAI;
using Azure.Identity;
using Manuals.Options;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Manuals.Services;

public sealed class AzureOpenAIChatClientFactory : IChatClientFactory
{
    private readonly AzureOpenAIClient _azureClient;
    private readonly string _deploymentName;

    public AzureOpenAIChatClientFactory(IOptions<AzureOpenAIOptions> options)
    {
        var opts = options.Value;
        _azureClient = new AzureOpenAIClient(
            new Uri(opts.Endpoint),
            new DefaultAzureCredential());
        _deploymentName = opts.DeploymentName;
    }

    public ChatClient CreateChatClient()
        => _azureClient.GetChatClient(_deploymentName);
}
