using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace Isoform.Server;

/// <summary>
/// Claude through the official Anthropic SDK, with the API key from the server's environment. The key goes only into
/// the request header: an error that quotes it has it replaced before the message goes anywhere.
/// </summary>
public sealed class ClaudeModel(string key, HttpClient? http = null) : IChatModel
{
    readonly AnthropicClient _client = http is null ? new() { ApiKey = key } : new() { ApiKey = key, HttpClient = http };

    /// <summary>The model from <c>ANTHROPIC_API_KEY</c>, or null when the environment has no key.</summary>
    public static ClaudeModel? FromEnvironment() =>
        Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } key ? new ClaudeModel(key) : null;

    /// <inheritdoc/>
    public async Task<BetaMessage> Reply(MessageCreateParams request, CancellationToken cancel)
    {
        try
        {
            return await _client.Beta.Messages.Create(request, cancel);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new InvalidOperationException(e.Message.Replace(key, "[ANTHROPIC_API_KEY]"));
        }
    }
}
