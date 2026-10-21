using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Isoform.Document;
using Isoform.Interactive;
using Isoform.Modeling;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Isoform.Server.Tests;

/// <summary>The kernel endpoint in process: over the WebSocket, an op replies with what the session and its JSON give directly.</summary>
public class KernelSocketTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Document = CanonicalFormat.Write(Examples.All[0].Doc);

    [Fact]
    public async Task A_regeneration_replies_with_result_v2_and_the_mesh_buffers()
    {
        using var socket = await Connect();
        var (bytes, type) = await Call(socket, new { id = 1, op = "regenerate", args = new { document = Document, cell = 0.5, chord = 0.05 } });
        var expected = new Session().Regenerate(Document, "[]", new ModelingSettings { CellSize = 0.5, ChordTolerance = 0.05 });
        using var v2 = JsonDocument.Parse(ViewJson.Write(expected));

        // The header's length, the header, then the buffers as float32 and int32.
        type.Should().Be(WebSocketMessageType.Binary);
        var length = BitConverter.ToInt32(bytes, 0);
        (length % 4).Should().Be(0);
        using var header = JsonDocument.Parse(bytes.AsMemory(4, length));
        var reply = header.RootElement;
        reply.GetProperty("id").GetInt32().Should().Be(1);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        reply.GetProperty("ms").GetDouble().Should().BePositive();
        var info = reply.GetProperty("result").GetProperty("info");
        info.GetProperty("cell").GetDouble().Should().Be(0.5);
        info.GetProperty("hash").GetString().Should().Be(expected.Hash);
        foreach (var name in new[] { "version", "slot", "format", "text", "diagnostics", "faces", "bodies", "edges", "vertices", "triangles", "lines", "smoothLines", "lineFaces", "refinement" })
            info.GetProperty(name).GetRawText().Should().Be(v2.RootElement.GetProperty(name).GetRawText(), name);

        var b = expected.Buffers;
        reply.GetProperty("lengths").EnumerateArray().Select(n => n.GetInt32()).Should().Equal(
            b.Positions.Length, b.Faces.Length, b.Lines.Length, b.SmoothLines.Length, b.LineEdges.Length, b.SmoothLineEdges.Length);
        bytes.Should().HaveCount(4 + length + 4 * (b.Positions.Length + b.Faces.Length + b.Lines.Length + b.SmoothLines.Length + b.LineEdges.Length + b.SmoothLineEdges.Length));
        var at = 4 + length;
        MemoryMarshal.Cast<byte, float>(bytes.AsSpan(at, 4 * b.Positions.Length)).ToArray().Should().Equal(b.Positions.Select(p => (float)p));
        at += 4 * b.Positions.Length;
        MemoryMarshal.Cast<byte, int>(bytes.AsSpan(at, 4 * b.Faces.Length)).ToArray().Should().Equal(b.Faces);
        at = bytes.Length - 4 * b.SmoothLineEdges.Length;
        MemoryMarshal.Cast<byte, int>(bytes.AsSpan(at)).ToArray().Should().Equal(b.SmoothLineEdges);
    }

    [Fact]
    public async Task Other_ops_reply_as_text_and_a_failure_names_the_problem()
    {
        using var socket = await Connect();
        var (examples, type) = await Call(socket, new { id = 2, op = "examples" });
        type.Should().Be(WebSocketMessageType.Text);
        using (var reply = JsonDocument.Parse(examples))
            reply.RootElement.GetProperty("result").GetRawText().Should().Be(ViewJson.Examples());

        var (broken, _) = await Call(socket, new { id = 3, op = "regenerate", args = new { document = "{", cell = 0.5, chord = 0.05 } });
        using (var reply = JsonDocument.Parse(broken))
            reply.RootElement.GetProperty("result").GetProperty("info").GetProperty("error").GetString().Should().NotBeNullOrEmpty();

        var (unknown, _) = await Call(socket, new { id = 4, op = "fly" });
        using (var reply = JsonDocument.Parse(unknown))
        {
            reply.RootElement.GetProperty("id").GetInt32().Should().Be(4);
            reply.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
            reply.RootElement.GetProperty("error").GetString().Should().Contain("fly");
        }
    }

    [Fact]
    public async Task Only_the_servers_own_page_may_open_a_kernel()
    {
        var own = await Connect($"http://{factory.Server.BaseAddress.Authority}");
        own.State.Should().Be(WebSocketState.Open);

        var other = () => Connect("https://some-other-site.example");
        await other.Should().ThrowAsync<Exception>("a page from another site must not reach the kernel");
    }

    Task<WebSocket> Connect(string? origin = null)
    {
        var client = factory.Server.CreateWebSocketClient();
        if (origin is not null) client.ConfigureRequest = r => r.Headers.Origin = origin;
        return client.ConnectAsync(new Uri(factory.Server.BaseAddress, "kernel"), CancellationToken.None);
    }

    static async Task<(byte[] Bytes, WebSocketMessageType Type)> Call(WebSocket socket, object request)
    {
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(request), WebSocketMessageType.Text, true, CancellationToken.None);
        using var message = new MemoryStream();
        var buffer = new byte[1 << 16];
        while (true)
        {
            var r = await socket.ReceiveAsync(buffer, CancellationToken.None);
            message.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return (message.ToArray(), r.MessageType);
        }
    }
}
