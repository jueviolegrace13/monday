using Monday.Application.Abstractions;
using Monday.Domain.Agent;

namespace Monday.Infrastructure.Models;

/// <summary>Builds the adapter for a project's provider. Its HttpClient ("models") dials only public addresses.</summary>
public sealed class ChatModelFactory(IHttpClientFactory clients) : IChatModelFactory
{
    public const string ClientName = "models";

    public IChatModel Create(ModelSettings settings, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Provider switch
        {
            ModelProvider.Anthropic => new AnthropicChatModel(clients.CreateClient(ClientName), settings, apiKey),
            ModelProvider.OpenAiCompatible => new OpenAiCompatibleChatModel(clients.CreateClient(ClientName), settings, apiKey),
            _ => throw new ModelException("model.unsupported", "This model provider is not supported yet."),
        };
    }
}
