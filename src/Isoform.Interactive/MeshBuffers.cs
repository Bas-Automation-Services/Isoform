using BasAutomation.Geometry;
using Isoform.Meshing;
using Isoform.Modeling;

namespace Isoform.Interactive;

/// <summary>
/// A mesh as the flat arrays a viewer reads straight into GPU buffers: three corners per triangle and its face, and the
/// lines where faces meet as segment end points, split into sharp edges and smooth (tangent) ones. Each segment knows
/// the pair of faces it separates, as an index into <see cref="LineFaces"/>, so picking a line gives an edge.
/// </summary>
public sealed class MeshBuffers
{
    public static MeshBuffers Empty { get; } = new([], [], [], [], [], [], []);

    MeshBuffers(double[] positions, int[] faces, double[] lines, int[] lineEdges, double[] smoothLines, int[] smoothLineEdges, int[] lineFaces) =>
        (Positions, Faces, Lines, LineEdges, SmoothLines, SmoothLineEdges, LineFaces) = (positions, faces, lines, lineEdges, smoothLines, smoothLineEdges, lineFaces);

    /// <summary>Nine coordinates per triangle.</summary>
    public double[] Positions { get; }

    /// <summary>The face index of each triangle.</summary>
    public int[] Faces { get; }

    /// <summary>Six coordinates per sharp segment.</summary>
    public double[] Lines { get; }

    /// <summary>Per sharp segment, the index of its face pair in <see cref="LineFaces"/>.</summary>
    public int[] LineEdges { get; }

    /// <summary>Six coordinates per smooth (tangent) segment.</summary>
    public double[] SmoothLines { get; }

    /// <summary>Per smooth segment, the index of its face pair in <see cref="LineFaces"/>.</summary>
    public int[] SmoothLineEdges { get; }

    /// <summary>The face pairs the lines separate, two face indices each (lower first), in order of first appearance.</summary>
    public int[] LineFaces { get; }

    public int Triangles => Faces.Length;

    // Faces meeting at less than this angle are tangent: drawn faintly, like the tangent edges in a CAD view.
    static readonly double SharpCos = Math.Cos(20 * Math.PI / 180);

    /// <summary>
    /// A view's mesh simplified face by face for display (<see cref="FaceSimplifier"/>): every part once, in its own
    /// coordinates, then placed. Faces are the view's, so picks resolve through the octree mesh as before.
    /// </summary>
    public static MeshBuffers Display(View view, double chord)
    {
        if (view.Part is { Mesh: not null } part)
        {
            var (mesh, faces) = Simplified(part, chord);
            return Of(mesh, faces);
        }
        if (view.Assembly is not { } assembly) return Of(view.Mesh, view.FaceOfQuad);
        var (vertices, quads, faceOf) = (new List<Point3D>(), new List<Quad>(), new List<int>());
        var parts = new Dictionary<FrepResult, (Mesh Mesh, int[] Faces)>(ReferenceEqualityComparer.Instance);
        var f0 = 0;
        foreach (var i in assembly.Instances)
        {
            if (i.Result.Mesh is not null)
            {
                if (!parts.TryGetValue(i.Result, out var s)) parts.Add(i.Result, s = Simplified(i.Result, chord));
                var v0 = vertices.Count;
                vertices.AddRange(s.Mesh.Vertices.Select(i.Placement.ToWorld));
                quads.AddRange(s.Mesh.Quads.Select(q => new Quad(q.A + v0, q.B + v0, q.C + v0, q.D + v0, q.Owner)));
                faceOf.AddRange(s.Faces.Select(f => f + f0));
            }
            f0 += i.Result.Topology?.Faces.Count ?? 0;
        }
        return Of(new Mesh(vertices, quads), faceOf);
    }

    static (Mesh, int[]) Simplified(FrepResult r, double chord) => FaceSimplifier.Simplify(r.Mesh!, r.FaceOfQuad, r.Part.Surfaces, r.Part.Field!, chord);

    // Plain arrays, integer keys and no closures: in WebAssembly, arrays of structs read through IReadOnlyList and
    // tuple keys are many times slower.
    public static MeshBuffers Of(Mesh? mesh, IReadOnlyList<int> faceOfQuad)
    {
        if (mesh is null) return Empty;
        var v = mesh.Vertices.ToArray();
        var quads = mesh.Quads.ToArray();
        var faceOf = faceOfQuad.ToArray();
        var (positions, faces) = Split(v, quads, faceOf);
        var faceCount = faceOf.Length == 0 ? 0 : faceOf.Max() + 1;
        var (lines, lineEdges, smooth, smoothEdges, pairs) = Edges(v, quads, faceOf, faceCount);
        return new MeshBuffers(positions, faces, lines, lineEdges, smooth, smoothEdges, pairs);
    }

    /// <summary>Three corners per triangle and its face; each quad split along its shorter diagonal.</summary>
    static (double[] Positions, int[] Faces) Split(Point3D[] v, Quad[] quads, int[] faceOf)
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

    static int Triangle(double[] positions, int[] faces, int t, Point3D a, Point3D b, Point3D c, int face)
    {
        Put(positions, 9 * t, a);
        Put(positions, 9 * t + 3, b);
        Put(positions, 9 * t + 6, c);
        faces[t] = face;
        return t + 1;
    }

    /// <summary>Mesh edges whose two sides belong to different faces, sharp or tangent by the quads' normals.</summary>
    static (double[] Sharp, int[] SharpEdges, double[] Smooth, int[] SmoothEdges, int[] Pairs) Edges(Point3D[] v, Quad[] quads, int[] faceOf, int faceCount)
    {
        var firstQuad = new Dictionary<long, int>(quads.Length * 2);
        var pairOf = new Dictionary<long, int>();
        var (sharp, smooth, sharpEdges, smoothEdges, pairs) = (new List<double>(), new List<double>(), new List<int>(), new List<int>(), new List<int>());
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
                var (f, g) = (faceOf[other], faceOf[q]);
                if (f == g) continue;
                var (lo, hi) = (Math.Min(f, g), Math.Max(f, g));
                if (!pairOf.TryGetValue((long)lo * faceCount + hi, out var pair))
                {
                    pairOf.Add((long)lo * faceCount + hi, pair = pairs.Count / 2);
                    pairs.Add(lo);
                    pairs.Add(hi);
                }
                var isSharp = Normal(v, quads[other]).Dot(Normal(v, quad)) < SharpCos;
                var into = isSharp ? sharp : smooth;
                Append(into, v[a]);
                Append(into, v[b]);
                (isSharp ? sharpEdges : smoothEdges).Add(pair);
            }
        return (sharp.ToArray(), sharpEdges.ToArray(), smooth.ToArray(), smoothEdges.ToArray(), pairs.ToArray());
    }

    static Vector3D Normal(Point3D[] v, Quad q) => (v[q.C] - v[q.A]).Cross(v[q.D] - v[q.B]).Normalized();

    static void Put(double[] into, int at, Point3D p)
    {
        into[at] = p.X;
        into[at + 1] = p.Y;
        into[at + 2] = p.Z;
    }

    static void Append(List<double> into, Point3D p)
    {
        into.Add(p.X);
        into.Add(p.Y);
        into.Add(p.Z);
    }
}
