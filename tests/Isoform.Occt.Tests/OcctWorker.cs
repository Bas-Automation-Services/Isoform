using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Occt.Tests;

/// <summary>
/// OCCT in a child process for the robustness runs: its fillet can recurse without end on a sliver, and a native stack
/// overflow takes the whole process down. The test assembly is its own worker (<c>--occt-worker</c>): one canonical
/// document per line in, one result per line out. A worker that dies or hangs is a crash, and the next call starts another.
/// </summary>
public static class OcctWorker
{
    /// <summary>The test assembly's entry point; the test runner never calls it.</summary>
    public static int Main(string[] args)
    {
        if (args is ["--occt-worker"])
            while (Console.ReadLine() is { } line)
                Console.WriteLine(JsonSerializer.Serialize(ToWire(Robustness.RunOcctHere(CanonicalFormat.Read(line)))));
        return 0;
    }

    sealed record Wire(Outcome Outcome, List<Diagnostic> Diagnostics, List<TopoFace>? Faces, List<TopoEdge>? Edges, List<int[]>? Vertices, int Bodies,
        double? Volume, double Ms, string? Error);

    static Wire ToWire(BackendRun r) => new(r.Outcome, r.Diagnostics.ToList(), r.Topology?.Faces.ToList(), r.Topology?.Edges.ToList(),
        r.Topology?.Vertices.Select(v => v.Faces.ToArray()).ToList(), r.Topology?.BodyCount ?? 0, r.Volume, r.Ms, r.Error);

    static BackendRun FromWire(Wire w) => new(w.Outcome, w.Diagnostics,
        w.Faces is null ? null : new Topology(w.Faces, w.Edges!, w.Vertices!.Select((f, i) => new TopoVertex(i, f)).ToList(), w.Bodies),
        w.Volume, true, w.Ms, w.Error);

    static Process? _worker;
    static readonly Lock Gate = new(); // test classes run in parallel; the worker answers one document at a time
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    public static BackendRun Run(PartDocument doc)
    {
        lock (Gate) return Ask(doc);
    }

    static BackendRun Ask(PartDocument doc)
    {
        _worker ??= Start();
        var line = JsonNode.Parse(CanonicalFormat.Write(doc))!.ToJsonString();
        try
        {
            _worker.StandardInput.WriteLine(line);
            var reply = _worker.StandardOutput.ReadLineAsync();
            if (reply.Wait(Patience) && reply.Result is { } json) return FromWire(JsonSerializer.Deserialize<Wire>(json)!);
        }
        catch (IOException) { }
        var exited = _worker.WaitForExit(100);
        var reason = exited ? $"OCCT process died (exit code {_worker.ExitCode:X})" : $"OCCT did not answer within {Patience.TotalSeconds:F0} s";
        Stop();
        return new(Outcome.Exception, [], null, null, false, 0, reason);
    }

    static Process Start()
    {
        var self = typeof(OcctWorker).Assembly.Location;
        var info = new ProcessStartInfo("dotnet", ["exec", self, "--occt-worker"])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        var p = Process.Start(info)!;
        p.ErrorDataReceived += (_, _) => { }; // drain the crash report so the worker never blocks on it
        p.BeginErrorReadLine();
        return p;
    }

    static void Stop()
    {
        try { _worker!.Kill(); } catch (InvalidOperationException) { }
        _worker!.Dispose();
        _worker = null;
    }
}
