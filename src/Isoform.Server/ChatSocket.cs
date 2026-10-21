using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Isoform.Interactive;

namespace Isoform.Server;

/// <summary>
/// The chat panel's connection (<c>/chat</c>), apart from the kernel's so a turn of a minute does not hold up
/// regenerations. The worker protocol's envelope: a request <c>{id, op: "chat", args: {document, messages}}</c> is
/// answered <c>{id, ok, result|error, ms}</c>, with <c>result</c> <c>{text, edits, hash, stop, toolCalls, error?}</c>
/// (<see cref="ChatResult"/>). While a turn runs, each tool call sends <c>{progress: id, tool, ok, calls, edits}</c>,
/// which has no <c>id</c>, so a client that does not know it ignores it. One turn at a time per connection.
/// </summary>
public static class ChatSocket
{
    /// <summary>The answer to a chat when the server has no model.</summary>
    public const string NoKey = "the server has no ANTHROPIC_API_KEY: set it in the environment the server starts in, and restart it";

    /// <summary>Serves one connection; <paramref name="model"/> is null when the server has no key.</summary>
    public static async Task Run(HttpContext context, IChatModel? model)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var cancel = context.RequestAborted;
        var message = new ArrayBufferWriter<byte>();
        while (true)
        {
            var received = await socket.ReceiveAsync(message.GetMemory(1 << 16), cancel);
            if (received.MessageType == WebSocketMessageType.Close) break;
            message.Advance(received.Count);
            if (!received.EndOfMessage) continue;
            var request = JsonNode.Parse(message.WrittenSpan)!;
            message.ResetWrittenCount();
            var reply = await Reply(request, model, p => Send(socket, p, cancel), cancel);
            await Send(socket, reply, cancel);
        }
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    /// <summary>The reply to one request; <paramref name="send"/> carries its progress messages.</summary>
    public static async Task<JsonObject> Reply(JsonNode request, IChatModel? model, Func<JsonObject, Task> send, CancellationToken cancel = default)
    {
        var start = Stopwatch.GetTimestamp();
        var id = request["id"]?.DeepClone();
        var reply = new JsonObject { ["id"] = id };
        try
        {
            var op = (string?)request["op"];
            if (op != "chat") throw new InvalidOperationException($"There is no op {op}.");
            if (model is null) throw new InvalidOperationException(NoKey);
            var args = request["args"]!;
            var messages = args["messages"]!.AsArray().Select(m => new ChatMessage((string)m!["role"]!, (string)m["text"]!)).ToList();
            var r = await new ChatAgent(model).Turn((string)args["document"]!, messages, p =>
            {
                p.Insert(0, "progress", id?.DeepClone());
                return send(p);
            }, cancel);
            reply["ok"] = true;
            reply["result"] = new JsonObject
            {
                ["text"] = r.Text, ["edits"] = new JsonArray([.. r.Edits.Select(e => e.DeepClone())]), ["hash"] = r.Hash,
                ["stop"] = r.Stop, ["toolCalls"] = r.ToolCalls, ["error"] = r.Error,
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            reply["ok"] = false;
            reply["error"] = AgentTools.Message(e);
        }
        reply["ms"] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return reply;
    }

    static Task Send(WebSocket socket, JsonObject message, CancellationToken cancel) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString(AgentTools.Compact)), WebSocketMessageType.Text, true, cancel);
}
