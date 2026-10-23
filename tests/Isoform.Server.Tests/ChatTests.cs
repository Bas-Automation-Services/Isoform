using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Beta.Messages;
using Isoform.Document;
using Isoform.Interactive;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Isoform.Server.Tests;

/// <summary>The chat: its tool-use loop against a scripted model, its tools on the working document, and its socket. No network.</summary>
public class ChatTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Plate = CanonicalFormat.Write(new PartDocument([FormatGuide.Plate]));
    static readonly ChatMessage[] Ask = [new("user", "make the plate 120 long")];

    [Fact]
    public async Task A_turn_that_applies_an_edit_returns_the_edit_and_the_new_hash()
    {
        var model = new ScriptedModel(
            ToolUse("t1", "apply_edits", """{"edits": [{"set": "plate.size.0", "value": 120}]}"""),
            Text("The plate is 120 long now."));
        var r = await new ChatAgent(model).Turn(Plate, Ask);

        r.Text.Should().Be("The plate is 120 long now.");
        r.Stop.Should().Be("end_turn");
        r.ToolCalls.Should().Be(1);
        r.Edits.Select(e => e.ToJsonString()).Should().Equal("""{"set":"plate.size.0","value":120}""");
        r.Hash.Should().Be(DocumentEdits.TryApply(Plate, """{"set": "plate.size.0", "value": 120}""").Hash);

        // The second call carries the first's tool call back, then its result; the document came with the question.
        var second = model.Requests[1].Messages;
        second.Should().HaveCount(3);
        Json(second[0]).Should().Contain("<document>").And.Contain("make the plate 120 long");
        Json(second[1]).Should().Contain("\"tool_use\"").And.Contain("t1");
        var result = JsonNode.Parse(Json(second[2]))!["content"]![0]!;
        ((string)result["type"]!, (string)result["tool_use_id"]!).Should().Be(("tool_result", "t1"));
        ((bool?)result["is_error"] ?? false).Should().BeFalse();
        model.Requests[0].Model.Raw().Should().Be("claude-opus-5-5");
    }

    [Fact]
    public async Task A_tool_error_goes_back_to_the_model_as_its_result()
    {
        var model = new ScriptedModel(
            ToolUse("t1", "apply_edits", """{"edits": [{"remove": "nope"}]}"""),
            Text("There is no feature nope."));
        var r = await new ChatAgent(model).Turn(Plate, Ask);

        r.Stop.Should().Be("end_turn");
        r.Edits.Should().BeEmpty();
        r.Hash.Should().Be(DocumentEdits.TryApply(Plate, "[]").Hash);
        var result = JsonNode.Parse(Json(model.Requests[1].Messages[2]))!["content"]![0]!;
        ((bool)result["is_error"]!).Should().BeTrue();
        ((string)result["content"]!).Should().Contain("'nope' is not a feature id");
    }

    [Fact]
    public async Task A_turn_stops_at_its_tool_call_limit_and_says_so()
    {
        var model = new ScriptedModel(_ => ToolUse(Guid.NewGuid().ToString("N"), "validate", "{}"));
        var r = await new ChatAgent(model).Turn(Plate, Ask);

        r.Stop.Should().Be("tool_limit");
        r.ToolCalls.Should().Be(ChatAgent.MaxToolCalls);
        r.Text.Should().Contain($"limit of {ChatAgent.MaxToolCalls} tool calls");
    }

    [Fact]
    public void A_sketch_call_settles_the_solution_as_edits()
    {
        // The outline of the format guide, fully constrained, with one corner stored off its solution.
        var outline = FormatGuide.Examples.Select(e => e.Feature).OfType<Sketch>().First();
        var moved = outline with { Points = new Dictionary<string, double[]>(outline.Points) { ["c"] = [93, 38] } };
        var document = CanonicalFormat.Write(new PartDocument([FormatGuide.Plate, moved]));
        var tools = new ChatTools(document);

        var solved = JsonNode.Parse(tools.Run("sketch", Input("""{"id": "outline"}""")))!;
        ((int)solved["freeDegrees"]!).Should().Be(0);
        ((int)solved["settled"]!["edits"]!).Should().BePositive();
        tools.Edits.Should().NotBeEmpty();
        DocumentEdits.Apply(document, tools.Edits).Should().Be(tools.Text);
        var c = CanonicalFormat.Read(tools.Text).Features.OfType<Sketch>().Single().Points["c"];
        c[0].Should().BeApproximately(90, 1e-9);
        c[1].Should().BeApproximately(35, 1e-9);

        // Solved again, nothing moves.
        JsonNode.Parse(tools.Run("sketch", Input("""{"id": "outline"}""")))!["settled"]!["edits"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public void A_sketch_edit_that_cannot_be_made_says_why_and_changes_nothing()
    {
        var document = CanonicalFormat.Write(new PartDocument([FormatGuide.Plate, FormatGuide.Examples.Select(e => e.Feature).OfType<Sketch>().First()]));
        var tools = new ChatTools(document);
        var before = tools.Text;

        var noRadius = () => tools.Run("sketch_edit", Input("""{"id": "outline", "op": "fillet", "corner": "c"}"""));
        var noOp = () => tools.Run("sketch_edit", Input("""{"id": "outline"}"""));
        noRadius.Should().Throw<ArgumentException>().WithMessage("*needs a radius*");
        noOp.Should().Throw<ToolException>().WithMessage("op is required");
        tools.Edits.Should().BeEmpty();
        tools.Text.Should().Be(before);
        ChatTools.Definitions.Select(d => d.Name).Should().Contain("sketch_edit");
    }

    [Fact]
    public void A_sketch_edit_is_stored_as_edits_for_the_page()
    {
        var document = CanonicalFormat.Write(new PartDocument([FormatGuide.Plate, FormatGuide.Examples.Select(e => e.Feature).OfType<Sketch>().First()]));
        var tools = new ChatTools(document);

        var r = JsonNode.Parse(tools.Run("sketch_edit", Input("""{"id": "outline", "op": "fillet", "corner": "c", "radius": 3}""")))!;
        ((int)r["settled"]!["edits"]!).Should().BePositive();
        r["added"]!.AsArray().Should().NotBeEmpty();
        DocumentEdits.Apply(document, tools.Edits).Should().Be(tools.Text);
        CanonicalFormat.Read(tools.Text).Features.OfType<Sketch>().Single().Entities.OfType<SketchArc>().Should().ContainSingle();
    }

    [Fact]
    public void A_projected_edge_is_stored_with_its_link()
    {
        var document = CanonicalFormat.Write(new PartDocument([FormatGuide.Plate, FormatGuide.Examples.Select(e => e.Feature).OfType<Sketch>().First()]));
        var tools = new ChatTools(document);

        var r = JsonNode.Parse(tools.Run("sketch_edit", Input("""{"id": "outline", "op": "project", "faces": ["plate/z+", "plate/y+"], "linked": true}""")))!;
        r["added"]!.AsArray().Select(n => (string)n!).Should().Contain("projected");
        DocumentEdits.Apply(document, tools.Edits).Should().Be(tools.Text);
        CanonicalFormat.Read(tools.Text).Features.OfType<Sketch>().Single().Entities.Single(e => e.Id == "projected").Project.Should().Equal("plate/z+", "plate/y+");
    }

    [Fact]
    public async Task Without_a_key_the_chat_says_so()
    {
        var server = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IChatModel>()));
        var (frames, reply) = await Chat(server);
        frames.Should().BeEmpty();
        ((bool)reply["ok"]!).Should().BeFalse();
        ((string)reply["error"]!).Should().Be(ChatSocket.NoKey).And.Contain("ANTHROPIC_API_KEY");
    }

    [Fact]
    public async Task The_key_goes_in_the_request_header_and_never_into_a_reply()
    {
        const string key = "test-key-7f3a9c";
        var api = new FakeApi(Response(ToolUse("t1", "apply_edits", """{"edits": [{"set": "plate.size.0", "value": 120}]}""")), Response(Text("Done.")));
        var server = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IChatModel>(new ClaudeModel(key, new HttpClient(api)))));
        var (frames, reply) = await Chat(server);

        ((bool)reply["ok"]!).Should().BeTrue(reply.ToJsonString());
        ((string)reply["result"]!["text"]!).Should().Be("Done.");
        reply["result"]!["edits"]!.AsArray().Should().HaveCount(1);
        // Progress carries no id, so a client that does not know it ignores it.
        frames.Should().ContainSingle().Which.Should().Match<JsonObject>(p => p["progress"]!.GetValue<int>() == 7 && p["id"] == null && (string)p["tool"]! == "apply_edits");
        foreach (var frame in frames.Append(reply)) frame.ToJsonString().Should().NotContain(key);

        // What went to the API: the key in its header, the model, the cached system prompt, the tools and the fallback.
        api.Keys.Should().AllBe(key);
        var body = JsonNode.Parse(api.Bodies[0])!;
        ((string)body["model"]!).Should().Be(ChatAgent.Model);
        body["system"]![0]!["cache_control"]!["type"]!.GetValue<string>().Should().Be("ephemeral");
        body["tools"]!.AsArray().Select(t => (string)t!["name"]!).Should().Equal(ChatTools.Definitions.Select(d => d.Name));
        ((string)body["fallbacks"]!).Should().Be("default");
        api.Bodies.Should().AllSatisfy(b => b.Should().NotContain(key));

        // An API error that quotes the key: the reply has it blanked.
        var failing = new FakeApi(Error(HttpStatusCode.Unauthorized, $"invalid x-api-key: {key}"));
        var broken = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IChatModel>(new ClaudeModel(key, new HttpClient(failing)))));
        var (_, error) = await Chat(broken);
        error.ToJsonString().Should().NotContain(key);
        ((string)error["result"]!["error"]!).Should().Contain("[ANTHROPIC_API_KEY]");
    }

    // ---- Scripted model and API ----

    /// <summary>A model that answers from a script and keeps the requests it was given.</summary>
    sealed class ScriptedModel(Func<int, string> reply) : IChatModel
    {
        public ScriptedModel(params string[] replies) : this(i => replies[i]) { }

        public List<MessageCreateParams> Requests { get; } = [];

        public Task<BetaMessage> Reply(MessageCreateParams request, CancellationToken cancel)
        {
            Requests.Add(request);
            return Task.FromResult(JsonSerializer.Deserialize<BetaMessage>(Message(reply(Requests.Count - 1)))!);
        }
    }

    /// <summary>The Messages API in process: the replies in order, and each request's key and body.</summary>
    sealed class FakeApi(params HttpResponseMessage[] replies) : HttpMessageHandler
    {
        public List<string> Keys { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Keys.Add(request.Headers.GetValues("x-api-key").Single());
            request.Headers.GetValues("anthropic-beta").Single().Should().Contain("server-side-fallback-2026-07-01");
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancel));
            return replies[Bodies.Count - 1];
        }
    }

    static string ToolUse(string id, string name, string input) =>
        $$"""{"content": [{"type": "tool_use", "id": "{{id}}", "name": "{{name}}", "input": {{input}}}], "stop_reason": "tool_use"}""";

    static string Text(string text) => $$"""{"content": [{"type": "text", "text": {{JsonSerializer.Serialize(text)}}}], "stop_reason": "end_turn"}""";

    // A whole message round the content and stop reason.
    static string Message(string part)
    {
        var o = JsonNode.Parse(part)!.AsObject();
        var m = new JsonObject
        {
            ["id"] = "msg_1", ["type"] = "message", ["role"] = "assistant", ["model"] = ChatAgent.Model,
            ["content"] = o["content"]!.DeepClone(), ["stop_reason"] = o["stop_reason"]!.DeepClone(), ["stop_sequence"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 },
        };
        return m.ToJsonString();
    }

    static HttpResponseMessage Response(string part) =>
        new(HttpStatusCode.OK) { Content = new StringContent(Message(part), Encoding.UTF8, "application/json") };

    static HttpResponseMessage Error(HttpStatusCode status, string message) => new(status)
    {
        Content = new StringContent(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "authentication_error", ["message"] = message } }.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    static IReadOnlyDictionary<string, JsonElement> Input(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    static string Json(BetaMessageParam m) => JsonSerializer.Serialize(m, AgentTools.Compact);

    /// <summary>One chat turn over the socket: the progress frames, then the reply.</summary>
    static async Task<(List<JsonObject> Frames, JsonObject Reply)> Chat(WebApplicationFactory<Program> server)
    {
        using var socket = await server.Server.CreateWebSocketClient().ConnectAsync(new Uri(server.Server.BaseAddress, "chat"), CancellationToken.None);
        var request = new { id = 7, op = "chat", args = new { document = Plate, messages = Ask.Select(m => new { role = m.Role, text = m.Text }) } };
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(request), WebSocketMessageType.Text, true, CancellationToken.None);
        var frames = new List<JsonObject>();
        var buffer = new byte[1 << 16];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await socket.ReceiveAsync(buffer, CancellationToken.None);
                message.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);
            var frame = JsonNode.Parse(message.ToArray())!.AsObject();
            if (frame["id"] is not null) return (frames, frame);
            frames.Add(frame);
        }
    }
}
