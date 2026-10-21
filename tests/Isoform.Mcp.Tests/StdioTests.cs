using System.Diagnostics;
using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Mcp;

namespace Isoform.Mcp.Tests;

/// <summary>One end-to-end run of the server over stdio, speaking raw JSON-RPC as a client would.</summary>
public class StdioTests
{
    [Fact]
    public async Task The_server_lists_its_tools_and_answers_a_call()
    {
        var start = new ProcessStartInfo("dotnet", $"\"{Path.Combine(AppContext.BaseDirectory, "Isoform.Mcp.dll")}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var server = Process.Start(start)!;
        server.ErrorDataReceived += (_, _) => { }; // logs go to stderr; drain them
        server.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        async Task<JsonNode> Call(int id, string method, JsonObject? parameters = null)
        {
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null) request["params"] = parameters;
            await server.StandardInput.WriteLineAsync(request.ToJsonString());
            await server.StandardInput.FlushAsync();
            while (true)
            {
                var line = await server.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException("the server closed stdout");
                var message = JsonNode.Parse(line)!;
                if ((int?)message["id"] == id) return message;
            }
        }

        try
        {
            var init = await Call(1, "initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
            });
            init["result"]!["serverInfo"]!["name"]!.GetValue<string>().Should().Be("isoform");
            await server.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            var tools = (await Call(2, "tools/list"))["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>());
            tools.Should().BeEquivalentTo("regenerate", "apply_edits", "query", "validate", "export_step", "assembly", "sketch", "describe_format");

            var call = await Call(3, "tools/call", new JsonObject
            {
                ["name"] = "validate",
                ["arguments"] = new JsonObject { ["document"] = CanonicalFormat.Write(new PartDocument([FormatGuide.Plate])) },
            });
            var text = call["result"]!["content"]![0]!["text"]!.GetValue<string>();
            JsonNode.Parse(text)!["ok"]!.GetValue<bool>().Should().BeTrue(text);

            var error = await Call(4, "tools/call", new JsonObject
            {
                ["name"] = "regenerate",
                ["arguments"] = new JsonObject { ["document"] = "{ not json" },
            });
            error["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        }
        finally
        {
            server.Kill();
        }
    }
}
