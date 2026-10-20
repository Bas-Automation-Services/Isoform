using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Isoform.Document;
using Isoform.Meshing;
using Isoform.Modeling;

namespace Isoform.Playground;

/// <summary>
/// What the page's workers call. <see cref="Regenerate"/> keeps the last result; the mesh then comes out as flat
/// arrays the page reads straight into WebGL buffers: three corners per triangle, a face index per triangle, and the
/// lines where faces meet as segment end points, split into sharp edges and smooth (tangent) ones. Region results
/// stay in a cache, so an edit re-meshes only the regions it changed; in a pool of workers each one meshes its
/// <see cref="Share"/> and the coordinating worker <see cref="Import"/>s the others' before it regenerates.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class Playground
{
    static double[] _positions = [];
    static int[] _faces = [];
    static double[] _lines = [];
    static double[] _smooth = [];
    static byte[] _share = [];
    static readonly RegionCache Cache = new();
    static FrepModeler? _modeler;
    static int _pooled; // regions the pool meshed for this regeneration outside Regenerate: own share and imports

    /// <summary>One modeler per settings, so the coordinator regenerates from the plans its share already made.</summary>
    static FrepModeler Modeler(ModelingSettings settings) =>
        _modeler is { } m && m.Settings == settings ? m : _modeler = new FrepModeler(settings, Cache);

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
        catch (Exception e) when (Unreadable(e))
        {
            (_positions, _faces, _lines, _smooth) = ([], [], [], []);
            Cache.Sweep();
            _pooled = 0;
            return Json(w => w.WriteString("error", e.Message));
        }
        var ms = clock.Elapsed.TotalMilliseconds;
        Flatten(view.Mesh, view.FaceOfQuad);
        var (reused, meshed) = Cache.Sweep();
        (reused, meshed, _pooled) = (reused - _pooled, meshed + _pooled, 0);

        return Json(w =>
        {
            w.WriteNumber("ms", ms);
            w.WriteNumber("reused", reused);
            w.WriteNumber("meshed", meshed);
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

    static bool Unreadable(Exception e) =>
        e is JsonException or FormatException or InvalidOperationException or NullReferenceException or KeyNotFoundException or InvalidCastException;

    /// <summary>
    /// Meshes share <paramref name="index"/> of <paramref name="count"/> of the regions of the document (of every part
    /// in a bundle) into the cache, and returns the results this worker had to mesh, for the coordinator to import.
    /// A document that does not read gives nothing: the coordinator reports the error.
    /// </summary>
    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<byte> Share(string document, double cellSize, double chordTolerance, int index, int count)
    {
        var modeler = Modeler(new ModelingSettings { CellSize = cellSize, ChordTolerance = chordTolerance });
        try
        {
            if (CanonicalFormat.FormatOf(document) != Bundle.FormatVersion) modeler.MeshShare(CanonicalFormat.Read(document), index, count);
            else
                foreach (var text in CanonicalFormat.ReadBundle(document).Documents.Values)
                    if (CanonicalFormat.FormatOf(text) != AssemblyDocument.FormatVersion) modeler.MeshShare(CanonicalFormat.Read(text), index, count);
        }
        catch (Exception e) when (Unreadable(e)) { }
        _pooled += Cache.TakeCounts().Misses;
        _share = Cache.TakeComputed();
        return _share;
    }

    /// <summary>Region results another worker meshed (its <see cref="Share"/>).</summary>
    [JSExport]
    public static void Import(byte[] regions) => _pooled += Cache.Import(regions);

    /// <summary>Ends a regeneration in a worker that only meshes shares, as <see cref="Regenerate"/> does in the coordinator.</summary>
    [JSExport]
    public static void Sweep()
    {
        Cache.Sweep();
        _pooled = 0;
    }

    static View Part(PartDocument doc, ModelingSettings settings)
    {
        var r = Modeler(settings).Regenerate(doc);
        var t = r.Topology;
        return new View(r.Mesh, r.FaceOfQuad, t?.Faces.Select(f => f.Sources).ToList() ?? [], r.Bodies.Select(b => (b.Name, b.Faces)).ToList(),
            r.Diagnostics, t?.Edges.Count ?? 0, t?.Vertices.Count ?? 0, r.Mesh?.ThinSpots.Select(s => s.Location) ?? []);
    }

    /// <summary>Every instance in place, its faces named by occurrence (<c>left/plate/z+</c>), one group per instance.</summary>
    static View Assembly(Bundle bundle, ModelingSettings settings)
    {
        var r = new AssemblyModeler(settings, Cache).Regenerate(bundle.Assembly, path => bundle.Documents.GetValueOrDefault(path));
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

    // Plain arrays, integer keys and no closures: in WebAssembly, arrays of structs read through IReadOnlyList and
    // tuple keys are many times slower.
    static void Flatten(Mesh? mesh, IReadOnlyList<int> faceOf)
    {
        if (mesh is null)
        {
            (_positions, _faces, _lines, _smooth) = ([], [], [], []);
            return;
        }
        var v = mesh.Vertices.ToArray();
        var quads = mesh.Quads.ToArray();
        var faceOfQuad = faceOf.ToArray();
        (_positions, _faces) = Triangles(v, quads, faceOfQuad);
        (_lines, _smooth) = Lines(v, quads, faceOfQuad);
    }

    /// <summary>Three corners per triangle and its face; each quad split along its shorter diagonal.</summary>
    static (double[] Positions, int[] Faces) Triangles(BasAutomation.Geometry.Point3D[] v, Quad[] quads, int[] faceOf)
    {
        var count = 0;
        foreach (var q in quads) count += q.IsTriangle ? 1 : 2;
        var positions = new double[count * 9];
        var faces = new int[count];
        var t = 0;
        for (var i = 0; i < quads.Length; i++)
        {
            var q = quads[i];
            var (a, b, c, d) = (q.A, q.B, q.C, q.D);
            if (q.IsTriangle) t = Triangle(positions, faces, t, v[a], v[b], v[c], faceOf[i]);
            else if ((v[a] - v[c]).LengthSquared <= (v[b] - v[d]).LengthSquared)
            {
                t = Triangle(positions, faces, t, v[a], v[b], v[c], faceOf[i]);
                t = Triangle(positions, faces, t, v[a], v[c], v[d], faceOf[i]);
            }
            else
            {
                t = Triangle(positions, faces, t, v[a], v[b], v[d], faceOf[i]);
                t = Triangle(positions, faces, t, v[b], v[c], v[d], faceOf[i]);
            }
        }
        return (positions, faces);
    }

    static int Triangle(double[] positions, int[] faces, int t, BasAutomation.Geometry.Point3D a, BasAutomation.Geometry.Point3D b, BasAutomation.Geometry.Point3D c, int face)
    {
        Put(positions, 9 * t, a);
        Put(positions, 9 * t + 3, b);
        Put(positions, 9 * t + 6, c);
        faces[t] = face;
        return t + 1;
    }

    /// <summary>Mesh edges whose two sides belong to different faces, sharp or tangent by the quads' normals.</summary>
    static (double[] Sharp, double[] Smooth) Lines(BasAutomation.Geometry.Point3D[] v, Quad[] quads, int[] faceOf)
    {
        var firstQuad = new Dictionary<long, int>(quads.Length * 2);
        var (sharp, smooth) = (new List<double>(), new List<double>());
        for (var q = 0; q < quads.Length; q++)
            for (var e = 0; e < 4; e++)
            {
                var quad = quads[q];
                var (a, b) = e switch { 0 => (quad.A, quad.B), 1 => (quad.B, quad.C), 2 => (quad.C, quad.D), _ => (quad.D, quad.A) };
                if (a == b) continue;
                // Not min << 32 | max: a long hashes as its two halves xor-ed, and neighbouring indices would collide.
                var key = (long)Math.Min(a, b) * v.Length + Math.Max(a, b);
                if (firstQuad.TryAdd(key, q)) continue;
                var other = firstQuad[key];
                if (faceOf[other] == faceOf[q]) continue;
                var into = Normal(v, quads[other]).Dot(Normal(v, quad)) < SharpCos ? sharp : smooth;
                Append(into, v[a]);
                Append(into, v[b]);
            }
        return (sharp.ToArray(), smooth.ToArray());
    }

    static BasAutomation.Geometry.Vector3D Normal(BasAutomation.Geometry.Point3D[] v, Quad q) => (v[q.C] - v[q.A]).Cross(v[q.D] - v[q.B]).Normalized();

    static void Put(double[] into, int at, BasAutomation.Geometry.Point3D p)
    {
        into[at] = p.X;
        into[at + 1] = p.Y;
        into[at + 2] = p.Z;
    }

    static void Append(List<double> into, BasAutomation.Geometry.Point3D p)
    {
        into.Add(p.X);
        into.Add(p.Y);
        into.Add(p.Z);
    }
}
