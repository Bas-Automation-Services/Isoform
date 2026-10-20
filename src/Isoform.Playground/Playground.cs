using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Isoform.Document;
using Isoform.Modeling;

namespace Isoform.Playground;

/// <summary>
/// What the page's worker calls. <see cref="Regenerate"/> keeps the last result; the mesh then comes out as flat
/// arrays the page reads straight into WebGL buffers: three corners per triangle, a face index per triangle, and the
/// lines where faces meet as segment end points, split into sharp edges and smooth (tangent) ones.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class Playground
{
    static double[] _positions = [];
    static int[] _faces = [];
    static double[] _lines = [];
    static double[] _smooth = [];

    // Faces meeting at less than this angle are tangent: drawn faintly, like the tangent edges in a CAD view.
    static readonly double SharpCos = Math.Cos(20 * Math.PI / 180);

    [JSExport]
    public static string Regenerate(string document, double cellSize, double chordTolerance)
    {
        PartDocument doc;
        try
        {
            doc = CanonicalFormat.Read(document);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NullReferenceException or KeyNotFoundException or InvalidCastException)
        {
            (_positions, _faces, _lines, _smooth) = ([], [], [], []);
            return Json(w => w.WriteString("error", e.Message));
        }

        var clock = Stopwatch.StartNew();
        var result = new FrepModeler(new ModelingSettings { CellSize = cellSize, ChordTolerance = chordTolerance }).Regenerate(doc);
        var ms = clock.Elapsed.TotalMilliseconds;
        Flatten(result);

        var t = result.Topology;
        return Json(w =>
        {
            w.WriteNumber("ms", ms);
            w.WriteNumber("triangles", _faces.Length);
            w.WriteNumber("bodies", t?.BodyCount ?? 0);
            w.WriteNumber("edges", t?.Edges.Count ?? 0);
            w.WriteNumber("vertices", t?.Vertices.Count ?? 0);
            w.WriteStartArray("faces");
            foreach (var f in t?.Faces ?? [])
            {
                w.WriteStartArray();
                foreach (var source in f.Sources) w.WriteStringValue(source);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteStartArray("bodies");
            foreach (var b in result.Bodies)
            {
                w.WriteStartObject();
                w.WriteString("name", b.Name);
                w.WriteStartArray("faces");
                foreach (var f in b.Faces) w.WriteNumberValue(f);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("diagnostics");
            foreach (var d in result.Diagnostics)
            {
                w.WriteStartObject();
                w.WriteString("code", d.Code);
                w.WriteString("feature", d.FeatureId);
                w.WriteString("message", d.Message);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("thinSpots");
            foreach (var spot in result.Mesh?.ThinSpots ?? [])
            {
                w.WriteStartArray();
                w.WriteNumberValue(spot.Location.X);
                w.WriteNumberValue(spot.Location.Y);
                w.WriteNumberValue(spot.Location.Z);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        });
    }

    // Written by hand: reflection-based serialization would not survive trimming of the published app.
    static string Json(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<double> Positions() => _positions;

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<int> Faces() => _faces;

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<double> Lines() => _lines;

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<double> SmoothLines() => _smooth;

    /// <summary>The example parts, name → canonical document text.</summary>
    [JSExport]
    public static string Examples() => Json(w =>
    {
        foreach (var (name, doc) in Isoform.Playground.Examples.All) w.WriteString(name, CanonicalFormat.Write(doc));
    });

    static void Flatten(FrepResult result)
    {
        if (result.Mesh is not { } mesh)
        {
            (_positions, _faces, _lines, _smooth) = ([], [], [], []);
            return;
        }
        var v = mesh.Vertices;
        var faceOf = result.FaceOfQuad;
        var positions = new List<double>(mesh.Quads.Count * 18);
        var faces = new List<int>(mesh.Quads.Count * 2);
        void Add(int a, int b, int c, int face)
        {
            foreach (var i in new[] { a, b, c }) positions.AddRange([v[i].X, v[i].Y, v[i].Z]);
            faces.Add(face);
        }

        // Lines: mesh edges whose two sides belong to different faces, sharp or tangent by the quads' normals.
        var firstQuad = new Dictionary<(int, int), int>();
        var (lines, smooth) = (new List<double>(), new List<double>());
        BasAutomation.Geometry.Vector3D Normal(Isoform.Meshing.Quad q) => (v[q.C] - v[q.A]).Cross(v[q.D] - v[q.B]).Normalized();
        void Edge(int a, int b, int q)
        {
            if (a == b) return;
            var key = (Math.Min(a, b), Math.Max(a, b));
            if (firstQuad.TryAdd(key, q) || faceOf[firstQuad[key]] == faceOf[q]) return;
            var sharp = Normal(mesh.Quads[firstQuad[key]]).Dot(Normal(mesh.Quads[q])) < SharpCos;
            (sharp ? lines : smooth).AddRange([v[a].X, v[a].Y, v[a].Z, v[b].X, v[b].Y, v[b].Z]);
        }

        for (var q = 0; q < mesh.Quads.Count; q++)
        {
            var quad = mesh.Quads[q];
            var face = faceOf[q];
            if (quad.IsTriangle) Add(quad.A, quad.B, quad.C, face);
            else if ((v[quad.A] - v[quad.C]).LengthSquared <= (v[quad.B] - v[quad.D]).LengthSquared)
            {
                Add(quad.A, quad.B, quad.C, face);
                Add(quad.A, quad.C, quad.D, face);
            }
            else
            {
                Add(quad.A, quad.B, quad.D, face);
                Add(quad.B, quad.C, quad.D, face);
            }
            Edge(quad.A, quad.B, q);
            Edge(quad.B, quad.C, q);
            Edge(quad.C, quad.D, q);
            Edge(quad.D, quad.A, q);
        }
        (_positions, _faces, _lines, _smooth) = (positions.ToArray(), faces.ToArray(), lines.ToArray(), smooth.ToArray());
    }
}
