using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Beta.Messages;
using Isoform.Interactive;

namespace Isoform.Server;

/// <summary>One message of the page's conversation: who said it (<c>user</c> or <c>assistant</c>) and the text.</summary>
public sealed record ChatMessage(string Role, string Text);

/// <summary>
/// What a turn did: the assistant's text, the edits it made to the page's document (in order), the hash of the
/// document they give, why it stopped (<c>end_turn</c>, <c>tool_limit</c>, <c>max_tokens</c>, <c>refusal</c>,
/// <c>error</c>), the number of tool calls, and the error when the model could not be reached.
/// </summary>
public sealed record ChatResult(string Text, IReadOnlyList<JsonNode> Edits, string Hash, string Stop, int ToolCalls, string? Error = null);

/// <summary>
/// The chat's tool-use loop: the page's document and conversation in, the model's answer and its edits out. Each
/// turn starts from the document the page sends; read-only tools run on the working text, edit tools change it, and the
/// edits come back for the page to commit as one history entry. Earlier turns are plain text, so a turn never replays
/// the model's thinking from another.
/// </summary>
public sealed class ChatAgent(IChatModel model)
{
    /// <summary>The model: the latest and most capable Claude model.</summary>
    public const string Model = "claude-opus-5-5";

    /// <summary>Tool calls one turn may make; a turn that wants more ends and says so.</summary>
    public const int MaxToolCalls = 20;

    /// <summary>
    /// The system prompt: how to work in the playground, then the format guide. The same text every turn, so it is
    /// cached with the tools.
    /// </summary>
    public static string SystemPrompt { get; } =
        "You are the modelling assistant in the Isoform playground: a CAD kernel driven by a JSON document. The user sees " +
        "the part in 3D beside this chat; the current document comes with each of their messages. Change the document " +
        "only through apply_edits (and sketch, which settles a solved sketch, and sketch_edit, which fillets a sketch's " +
        "corner or trims a piece off it); your edits are committed as one history " +
        "entry when your turn ends, so the user can see and undo them together. Check your work with regenerate or " +
        "validate, and learn face names from regenerate before referring to faces. Ids become face names: keep them " +
        "short and meaningful. For a sketch, add its points, entities and constraints with apply_edits, then call " +
        "sketch to solve it and see its free degrees of freedom and problems. Answer briefly in plain text: what you " +
        "changed and anything the user should know (diagnostics, free degrees of freedom, constraints a sketch edit " +
        "dropped); do not paste the document." +
        "\n\n" + FormatGuide.Text;

    static readonly List<BetaToolUnion> Tools = [.. ChatTools.Definitions.Select(d =>
    {
        var schema = JsonDocument.Parse(d.Schema).RootElement;
        return (BetaToolUnion)new BetaTool
        {
            Name = d.Name,
            Description = d.Description,
            InputSchema = new()
            {
                Properties = schema.GetProperty("properties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
                Required = schema.TryGetProperty("required", out var r) ? [.. r.EnumerateArray().Select(n => n.GetString()!)] : null,
            },
        };
    })];

    /// <summary>
    /// One turn: the conversation's last message is the user's, answered with the tools on <paramref name="document"/>.
    /// <paramref name="progress"/> hears of each tool call as it finishes. A tool that fails is reported to the model
    /// as an error result, which it can act on; a model call that fails ends the turn with the edits made so far.
    /// </summary>
    public async Task<ChatResult> Turn(string document, IReadOnlyList<ChatMessage> conversation, Func<JsonObject, Task>? progress = null, CancellationToken cancel = default)
    {
        var tools = new ChatTools(document);
        var messages = Messages(tools.Text, conversation);
        var text = new List<string>();
        var calls = 0;
        while (true)
        {
            BetaMessage reply;
            try { reply = await model.Reply(Request(messages), cancel); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return new ChatResult(string.Join("\n\n", text), tools.Edits, tools.Hash, "error", calls, e.Message);
            }
            var content = Echo(reply.Content);
            text.AddRange(content.Select(b => b.TryPickText(out var t) ? t.Text : null).OfType<string>());
            var uses = content.Select(b => b.TryPickToolUse(out var u) ? u : null).OfType<BetaToolUseBlockParam>().ToList();
            var stop = reply.StopReason?.Raw() ?? "end_turn";
            if (stop != "tool_use" || uses.Count == 0) return Done(stop);
            if (calls + uses.Count > MaxToolCalls) return Done("tool_limit");

            messages.Add(new BetaMessageParam { Role = Role.Assistant, Content = content });
            var results = new List<BetaContentBlockParam>();
            foreach (var use in uses)
            {
                calls++;
                string output;
                var failed = false;
                try { output = tools.Run(use.Name, use.Input); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    (output, failed) = (AgentTools.Message(e), true);
                }
                results.Add(new BetaToolResultBlockParam { ToolUseID = use.ID, Content = output, IsError = failed });
                if (progress is not null)
                    await progress(new JsonObject { ["tool"] = use.Name, ["ok"] = !failed, ["calls"] = calls, ["edits"] = tools.Edits.Count });
            }
            messages.Add(new BetaMessageParam { Role = Role.User, Content = results });
        }

        ChatResult Done(string stop)
        {
            var note = stop switch
            {
                "tool_limit" => $"(Stopped: this turn reached its limit of {MaxToolCalls} tool calls. The edits so far are applied; ask me to continue.)",
                "max_tokens" => "(Stopped: the answer reached its length limit.)",
                "refusal" => "(Claude declined this request.)",
                _ => null,
            };
            if (note is not null) text.Add(note);
            return new ChatResult(string.Join("\n\n", text), tools.Edits, tools.Hash, stop, calls);
        }
    }

    /// <summary>
    /// The request for the conversation so far: the cached system prompt and tools, adaptive thinking (the model's
    /// default), high effort, and the server's default fallback model should Claude decline.
    /// </summary>
    static MessageCreateParams Request(List<BetaMessageParam> messages) => new()
    {
        Model = Model,
        MaxTokens = 16000,
        System = new List<BetaTextBlockParam> { new() { Text = SystemPrompt, CacheControl = new BetaCacheControlEphemeral() } },
        Tools = Tools,
        Messages = messages,
        OutputConfig = new BetaOutputConfig { Effort = Effort.High },
        // Caches the conversation as it grows: each call of the loop reads the one before.
        CacheControl = new BetaCacheControlEphemeral(),
        Betas = ["server-side-fallback-2026-07-01"],
        Fallbacks = new Default(),
    };

    /// <summary>
    /// The conversation as API messages: plain text, the document (compact) put before the user's last message. The
    /// roles alternate, starting and ending with the user.
    /// </summary>
    static List<BetaMessageParam> Messages(string document, IReadOnlyList<ChatMessage> conversation)
    {
        if (conversation.Count == 0 || conversation.Count % 2 == 0 || conversation.Where((m, i) => m.Role != (i % 2 == 0 ? "user" : "assistant")).Any())
            throw new ToolException("a conversation alternates user and assistant messages, starting and ending with the user's");
        var compact = JsonNode.Parse(document)!.ToJsonString(AgentTools.Compact);
        return [.. conversation.Select((m, i) => new BetaMessageParam
        {
            Role = m.Role == "user" ? Role.User : Role.Assistant,
            Content = i == conversation.Count - 1 ? $"<document>\n{compact}\n</document>\n\n{m.Text}" : m.Text,
        })];
    }

    /// <summary>
    /// The reply's content as it goes back in the next request, thinking with its signature. After a fallback (a
    /// <c>fallback</c> block, where another model took over) only the text before it goes back.
    /// </summary>
    static List<BetaContentBlockParam> Echo(IReadOnlyList<BetaContentBlock> content)
    {
        var boundary = -1;
        for (var i = 0; i < content.Count; i++)
            if (content[i].TryPickFallback(out _)) boundary = i;
        var echo = new List<BetaContentBlockParam>();
        for (var i = 0; i < content.Count; i++)
        {
            var block = content[i];
            if (block.TryPickText(out var text)) echo.Add(new BetaTextBlockParam { Text = text.Text });
            else if (i < boundary) continue;
            else if (block.TryPickThinking(out var thinking)) echo.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            else if (block.TryPickRedactedThinking(out var redacted)) echo.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
            else if (block.TryPickToolUse(out var use)) echo.Add(new BetaToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
        }
        return echo;
    }
}

/// <summary>The model behind the chat: one Messages API call. Tests give a scripted one; the server gives <see cref="ClaudeModel"/>.</summary>
public interface IChatModel
{
    /// <summary>The model's reply to a request.</summary>
    Task<BetaMessage> Reply(MessageCreateParams request, CancellationToken cancel);
}
