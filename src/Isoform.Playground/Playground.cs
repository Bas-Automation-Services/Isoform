using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Isoform.Document;
using Isoform.Meshing;
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

    /// <summary>What the page shows: one mesh, a face index per quad, every face's names, and groups (bodies or instances).</summary>
    sealed record View(Mesh? Mesh, IReadOnlyList<int> FaceOfQuad, IReadOnlyList<IReadOnlyList<string>> Faces, IReadOnlyList<(string Name, IReadOnlyList<int> Faces)> Groups,
        IReadOnlyList<Diagnostic> Diagnostics, int Edges, int Vertices, IEnumerable<BasAutomation.Geometry.Point3D> ThinSpots);

    /// <summary>A part document, or a bundle of an assembly and its documents.</summary>
    [JSExport]
    public static string Regenerate(string document, double cellSize, double chordTolerance)
    {
        var settings = new ModelingSettings { CellSize = cellSize, ChordTolerance = chordTolerance };
        var clock = Stopwatch.StartNew();
        View view;
        try
        {
            view = CanonicalFormat.FormatOf(document) == Bundle.FormatVersion ? Assembly(CanonicalFormat.ReadBundle(document), settings) : Part(CanonicalFormat.Read(document), settings);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NullReferenceException or KeyNotFoundException or InvalidCastException)
        {
            (_positions, _faces, _lines, _smooth) = ([], [], [], []);
            return Json(w => w.WriteString("error", e.Message));
        }
        var ms = clock.Elapsed.TotalMilliseconds;
        Flatten(view.Mesh, view.FaceOfQuad);

        return Json(w =>
        {
            w.WriteNumber("ms", ms);
            w.WriteNumber("triangles", _faces.Length);
            w.WriteNumber("edges", view.Edges);
            w.WriteNumber("vertices", view.Vertices);
            w.WriteStartArray("faces");
            foreach (var f in view.Faces)
            {
                w.WriteStartArray();
                foreach (var source in f) w.WriteStringValue(source);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteStartArray("bodies");
            foreach (var (name, faces) in view.Groups)
            {
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteStartArray("faces");
                foreach (var f in faces) w.WriteNumberValue(f);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("diagnostics");
            foreach (var d in view.Diagnostics)
            {
                w.WriteStartObject();
                w.WriteString("code", d.Code);
                w.WriteString("feature", d.FeatureId);
                w.WriteString("message", d.Message);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("thinSpots");
            foreach (var spot in view.ThinSpots)
            {
                w.WriteStartArray();
                w.WriteNumberValue(spot.X);
                w.WriteNumberValue(spot.Y);
                w.WriteNumberValue(spot.Z);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        });
    }

    static View Part(PartDocument doc, ModelingSettings settings)
    {
        var r = new FrepModeler(settings).Regenerate(doc);
        var t = r.Topology;
        return new View(r.Mesh, r.FaceOfQuad, t?.Faces.Select(f => f.Sources).ToList() ?? [], r.Bodies.Select(b => (b.Name, b.Faces)).ToList(),
            r.Diagnostics, t?.Edges.Count ?? 0, t?.Vertices.Count ?? 0, r.Mesh?.ThinSpots.Select(s => s.Location) ?? []);
    }

    /// <summary>Every instance in place, its faces named by occurrence (<c>left/plate/z+</c>), one group per instance.</summary>
    static View Assembly(Bundle bundle, ModelingSettings settings)
    {
        var r = new AssemblyModeler(settings).Regenerate(bundle.Assembly, path => bundle.Documents.GetValueOrDefault(path));
        var (mesh, _) = r.WorldMesh();
        var (faceOfQuad, faces, groups) = (new List<int>(), new List<IReadOnlyList<string>>(), new List<(string, IReadOnlyList<int>)>());
        var (edges, vertices) = (0, 0);
        foreach (var i in r.Instances)
        {
            var f0 = faces.Count;
            var t = i.Result.Topology;
            faceOfQuad.AddRange(i.Result.FaceOfQuad.Select(f => f + f0));
            faces.AddRange(t?.Faces.Select(f => (IReadOnlyList<string>)f.Sources.Select(i.Name).ToList()) ?? []);
            groups.Add((i.Id, Enumerable.Range(f0, faces.Count - f0).ToList()));
            (edges, vertices) = (edges + (t?.Edges.Count ?? 0), vertices + (t?.Vertices.Count ?? 0));
        }
        var spots = r.Instances.SelectMany(i => i.Result.Mesh?.ThinSpots.Select(s => i.Placement.ToWorld(s.Location)) ?? []);
        return new View(mesh, faceOfQuad, faces, groups, r.Diagnostics, edges, vertices, spots);
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
        foreach (var (name, bundle) in Isoform.Playground.Examples.Bundles) w.WriteString(name, CanonicalFormat.Write(bundle));
    });

    static void Flatten(Mesh? mesh, IReadOnlyList<int> faceOf)
    {
        if (mesh is null)
        {
            (_positions, _faces, _lines, _smooth) = ([], [], [], []);
            return;
        }
        var v = mesh.Vertices;
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
