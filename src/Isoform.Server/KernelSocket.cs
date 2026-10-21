using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Isoform.Interactive;
using Isoform.Modeling;

namespace Isoform.Server;

/// <summary>
/// One WebSocket connection: the page's kernel worker (<c>src/Isoform.Playground/wwwroot/js/kernel-worker.js</c>) over a
/// session of its own, its ops done as the playground's exports do them. Requests <c>{id, op, args}</c> come as text and
/// are answered one at a time, as a worker does; a reply <c>{id, ok, result|error, ms}</c> goes back as text, except a
/// regeneration with a mesh, which goes as one binary message (<see cref="Pack"/>).
/// </summary>
/// <remarks>
/// The page opens two connections, like its two kinds of worker: one for what is committed and one for a drag's
/// previews, since the region cache keeps only what the last regeneration used. There is no pool: the kernel meshes on
/// every core itself, so the pool op does nothing.
/// </remarks>
public sealed class KernelSocket
{
    readonly Session _session = new();
    MeshBuffers? _mesh; // the last op's mesh, for its reply

    public static async Task Run(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var kernel = new KernelSocket();
        var message = new ArrayBufferWriter<byte>();
        while (true)
        {
            var received = await socket.ReceiveAsync(message.GetMemory(1 << 16), CancellationToken.None);
            if (received.MessageType == WebSocketMessageType.Close) break;
            message.Advance(received.Count);
            if (!received.EndOfMessage) continue;
            var (reply, binary) = kernel.Reply(message.WrittenMemory);
            message.ResetWrittenCount();
            await socket.SendAsync(reply, binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, CancellationToken.None);
        }
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    /// <summary>The reply to one request, and whether it is binary. An op that throws replies with its message, as the worker does.</summary>
    public (byte[] Bytes, bool Binary) Reply(ReadOnlyMemory<byte> request)
    {
        var start = Stopwatch.GetTimestamp();
        using var json = JsonDocument.Parse(request);
        var root = json.RootElement;
        var args = root.TryGetProperty("args", out var a) ? a : default;
        _mesh = null;
        string? result = null, error = null;
        try { result = Op(root.GetProperty("op").GetString(), args); }
        catch (Exception e) { error = e.Message; }

        var header = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(header))
        {
            w.WriteStartObject();
            w.WritePropertyName("id");
            root.GetProperty("id").WriteTo(w);
            w.WriteBoolean("ok", error is null);
            if (result is not null)
            {
                w.WritePropertyName("result");
                w.WriteRawValue(result, skipInputValidation: true);
            }
            else w.WriteString("error", error);
            w.WriteNumber("ms", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if (_mesh is { } m)
            {
                w.WriteStartArray("lengths");
                foreach (var n in (ReadOnlySpan<int>)[m.Positions.Length, m.Faces.Length, m.Lines.Length, m.SmoothLines.Length, m.LineEdges.Length, m.SmoothLineEdges.Length])
                    w.WriteNumberValue(n);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return _mesh is null ? (header.WrittenSpan.ToArray(), false) : (Pack(header.WrittenSpan, _mesh), true);
    }

    /// <summary>An op's result as JSON text: what the worker's op of that name returns.</summary>
    string Op(string? op, JsonElement args) => op switch
    {
        "examples" => ViewJson.Examples(),
        "apply" => ViewJson.Apply(Arg(args, "document")!.Value.GetString()!, Arg(args, "edits")!.Value.GetRawText()),
        "diff" => ViewJson.Diff(Arg(args, "before")!.Value.GetString()!, Arg(args, "after")!.Value.GetString()!),
        "handles" => HandleJson.Write(Slot(args), _session[Slot(args)]),
        "resolve" => ReferencePins.Json(Arg(args, "refs")!.Value.GetRawText(), _session[Slot(args)]),
        "sketch" => SketchJson.Write(Arg(args, "document")!.Value.GetString()!, Arg(args, "id")!.Value.GetString()!, Arg(args, "drag") is { ValueKind: JsonValueKind.Object } d ? d.GetRawText() : ""),
        "faceFrame" => SketchJson.FaceFrame(_session[Slot(args)], Arg(args, "face")!.Value.GetString()!),
        // The worker gives its wasm heap; here it is the process's managed heap, which both connections share.
        "memory" => GC.GetTotalMemory(false).ToString(CultureInfo.InvariantCulture),
        "pool" => "null",
        "regenerate" => Regenerate(args),
        _ => throw new InvalidOperationException($"There is no op {op}."),
    };

    /// <summary>Result v2 as <c>{info}</c>, the mesh kept for the reply when there is one.</summary>
    string Regenerate(JsonElement args)
    {
        var slot = Slot(args);
        var cell = Arg(args, "cell")!.Value;
        var settings = Settings(cell.GetDouble(), Arg(args, "chord")!.Value.GetDouble(), Arg(args, "snap")?.GetBoolean() ?? false, slot);
        var r = _session.Regenerate(Arg(args, "document")!.Value.GetString()!, Arg(args, "edits")?.GetRawText() ?? "[]", settings, slot);
        if (r.View is not null) _mesh = r.Buffers;
        // Like the worker, info carries the cell size it was made at (the diagnostics' resolve compares with it):
        // added inside result v2's closing brace.
        var info = ViewJson.Write(r);
        return $"{{\"info\":{info[..^1]},\"cell\":{cell.GetRawText()}}}}}";
    }

    // As the playground's exports (src/Isoform.Playground/Playground.cs): previews are not refined, they have a time
    // budget and their coarse cells already mark what is below the resolution.
    static ModelingSettings Settings(double cellSize, double chordTolerance, bool snapGrid, string slot) =>
        new() { CellSize = cellSize, ChordTolerance = chordTolerance, SnapGrid = snapGrid, Refinement = slot == Session.Preview ? 0 : new ModelingSettings().Refinement };

    static JsonElement? Arg(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) ? v : null;

    static string Slot(JsonElement args) => Arg(args, "slot")?.GetString() ?? Session.Main;

    /// <summary>
    /// A reply with a mesh: the header's length (int32), the header (JSON, padded with spaces to a multiple of four
    /// bytes), then the buffers in the order of its <c>lengths</c>: positions, faces, sharp lines, smooth lines, sharp
    /// line edges, smooth line edges. Coordinates are float32, as the page draws them; indices int32; little-endian.
    /// </summary>
    static byte[] Pack(ReadOnlySpan<byte> header, MeshBuffers m)
    {
        var padded = (header.Length + 3) & ~3;
        var bytes = new byte[4 + padded + 4 * (m.Positions.Length + m.Faces.Length + m.Lines.Length + m.SmoothLines.Length + m.LineEdges.Length + m.SmoothLineEdges.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, padded);
        header.CopyTo(bytes.AsSpan(4));
        bytes.AsSpan(4 + header.Length, padded - header.Length).Fill((byte)' ');
        var at = 4 + padded;
        at = Floats(bytes, at, m.Positions);
        at = Ints(bytes, at, m.Faces);
        at = Floats(bytes, at, m.Lines);
        at = Floats(bytes, at, m.SmoothLines);
        at = Ints(bytes, at, m.LineEdges);
        Ints(bytes, at, m.SmoothLineEdges);
        return bytes;
    }

    // The server runs on x64, little-endian like the page's typed arrays.
    static int Floats(byte[] into, int at, double[] values)
    {
        var to = MemoryMarshal.Cast<byte, float>(into.AsSpan(at, 4 * values.Length));
        for (var i = 0; i < values.Length; i++) to[i] = (float)values[i];
        return at + 4 * values.Length;
    }

    static int Ints(byte[] into, int at, int[] values)
    {
        MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(into.AsSpan(at));
        return at + 4 * values.Length;
    }
}
