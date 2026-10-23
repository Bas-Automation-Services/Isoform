using System.Diagnostics;
using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Interactive;
using Isoform.Meshing;
using Isoform.Modeling;
using Xunit.Abstractions;

namespace Isoform.Tests;

/// <summary>
/// T: the display mesh simplified face by face. Watertight and outward, every triangle on its own face, vertices the
/// mesh's own, new triangles within the chord of their surface, planar parts' volume unchanged, the same on repeat.
/// </summary>
public class FaceSimplifierTests(ITestOutputHelper output)
{
    static readonly ModelingSettings Settings = new();

    public static IEnumerable<object[]> Parts =>
        Examples.All.Select(e => new object[] { e.Name }).Append(["bench/20 operations"]);

    // Not static: the document suite (TestDocuments) names the examples after Examples, not this.
    PartDocument Part(string name) => name == "bench/20 operations" ? TapeTests.Twenty : Examples.All.Single(e => e.Name == name).Doc;

    static (FrepResult Result, Mesh Mesh, int[] Faces, double Ms) Simplified(PartDocument doc, ModelingSettings settings)
    {
        var r = new FrepModeler(settings).Regenerate(doc);
        var clock = Stopwatch.StartNew();
        var (mesh, faces) = FaceSimplifier.Simplify(r.Mesh!, r.FaceOfQuad, r.Part.Surfaces, r.Part.Field!, settings.ChordTolerance);
        return (r, mesh, faces, clock.Elapsed.TotalMilliseconds);
    }

    [Theory]
    [MemberData(nameof(Parts))]
    public void Simplified_meshes_stay_closed_on_their_faces_and_surfaces(string name)
    {
        var doc = Part(name);
        var (r, mesh, faces, ms) = Simplified(doc, Settings);
        var before = r.Mesh!.Triangles().Count();
        output.WriteLine($"{name}: {before:N0} → {mesh.Quads.Count:N0} triangles in {ms:F0} ms");

        mesh.IsWatertight().Should().BeTrue();
        mesh.Quads.Should().OnlyContain(q => q.IsTriangle);
        faces.Should().HaveCount(mesh.Quads.Count);
        SignedVolume(mesh).Should().BeApproximately(SignedVolume(r.Mesh), 1e-3 * SignedVolume(r.Mesh), "outward, and the volume barely moves");

        // Vertices are the mesh's own, so they stay on their surfaces.
        var own = r.Mesh.Vertices.Select(Bits).ToHashSet();
        mesh.Vertices.Should().OnlyContain(v => own.Contains(Bits(v)));

        // Every triangle is on its face: its corners are corners of that face, and faces meet where they met.
        var cornersOf = FaceCorners(r.Mesh, r.FaceOfQuad);
        for (var t = 0; t < mesh.Quads.Count; t++)
        {
            var q = mesh.Quads[t];
            foreach (var v in new[] { q.A, q.B, q.C }) cornersOf[faces[t]].Should().Contain(Bits(mesh.Vertices[v]));
        }
        Neighbours(mesh, faces).Should().BeEquivalentTo(Neighbours(r.Mesh, r.FaceOfQuad));

        // New triangles stay within the chord of their face's surface, sampled more finely than the pass does.
        var oldTriangles = r.Mesh.Triangles().Select(t => Key(r.Mesh, t.A, t.B, t.C)).ToHashSet();
        var (worst, worstField) = (0.0, 0.0);
        for (var t = 0; t < mesh.Quads.Count; t++)
        {
            var q = mesh.Quads[t];
            if (oldTriangles.Contains(Key(mesh, q.A, q.B, q.C))) continue;
            var s = r.Part.Surfaces[q.Owner];
            var onField = s.Kind is SurfaceKind.Blend or SurfaceKind.Lattice;
            var (a, b, c) = (mesh.Vertices[q.A], mesh.Vertices[q.B], mesh.Vertices[q.C]);
            for (var i = 0; i <= 6; i++)
            for (var j = 0; i + j <= 6; j++)
            {
                var p = a + (b - a) * (i / 6.0) + (c - a) * (j / 6.0);
                var d = Distance(r.Part, s, p);
                if (onField) worstField = Math.Max(worstField, d);
                else worst = Math.Max(worst, d);
            }
        }
        output.WriteLine($"  worst distance of a new triangle from its surface: {worst:E2}, from a blend or lattice {worstField:E2} (chord {Settings.ChordTolerance})");
        worst.Should().BeLessThanOrEqualTo(Settings.ChordTolerance);
        // A blend's or lattice's field is no distance (a blend's has creases where it meets its faces): |f| / |∇f| is
        // only near it, and between the pass's samples it can stray a little further.
        worstField.Should().BeLessThanOrEqualTo(1.25 * Settings.ChordTolerance);
    }

    [Fact]
    public void Planar_parts_keep_their_volume_and_lose_their_interior_vertices()
    {
        var doc = new PartDocument(
        [
            new Block("plate", BooleanOp.New, [0, 0, 0], [100, 50, 10]),
            new Block("pocket", BooleanOp.Cut, [20, 5, 6], [20, 10, 5]),
            new Block("slot", BooleanOp.Cut, [60, 30, -1], [6, 30, 12]),
            new Block("boss", BooleanOp.Join, [40, 30, 9], [10, 10, 8]),
            new PlaneCut("chamfer", [100, 0, 10], [0.7071067811865476, -0.7071067811865476, 0]),
        ]);
        var (r, mesh, _, _) = Simplified(doc, Settings);
        output.WriteLine($"{r.Mesh!.Triangles().Count():N0} → {mesh.Quads.Count:N0} triangles, {mesh.Vertices.Count} vertices");

        mesh.IsWatertight().Should().BeTrue();
        SignedVolume(mesh).Should().BeApproximately(SignedVolume(r.Mesh), 1e-9 * SignedVolume(r.Mesh));
        mesh.Vertices.Count.Should().Be(r.Topology!.Vertices.Count, "only the corners are left");
    }

    [Fact]
    public void A_repeat_gives_the_bit_identical_mesh()
    {
        var (_, a, fa, _) = Simplified(TapeTests.Twenty, Settings);
        var (_, b, fb, _) = Simplified(TapeTests.Twenty, Settings);

        b.Vertices.Select(Bits).Should().Equal(a.Vertices.Select(Bits));
        b.Quads.Should().Equal(a.Quads);
        fb.Should().Equal(fa);
    }

    static double Distance(CompiledPart part, Surface s, Point3D p)
    {
        if (s.Analytic) return Math.Abs(s.Distance(p));
        var sample = part.Field!.SampleSurface(s.Id, p)!.Value;
        return Math.Abs(sample.Value) / sample.Gradient.Length;
    }

    static Dictionary<int, HashSet<(long, long, long)>> FaceCorners(Mesh mesh, IReadOnlyList<int> faceOfQuad)
    {
        var corners = new Dictionary<int, HashSet<(long, long, long)>>();
        for (var q = 0; q < mesh.Quads.Count; q++)
        {
            if (!corners.TryGetValue(faceOfQuad[q], out var set)) corners[faceOfQuad[q]] = set = [];
            var quad = mesh.Quads[q];
            foreach (var v in new[] { quad.A, quad.B, quad.C, quad.D }) set.Add(Bits(mesh.Vertices[v]));
        }
        return corners;
    }

    /// <summary>The pairs of faces that share a mesh edge.</summary>
    static HashSet<(int, int)> Neighbours(Mesh mesh, IReadOnlyList<int> faceOf)
    {
        var first = new Dictionary<(int, int), int>();
        var pairs = new HashSet<(int, int)>();
        for (var q = 0; q < mesh.Quads.Count; q++)
        {
            var quad = mesh.Quads[q];
            int[] c = [quad.A, quad.B, quad.C, quad.D];
            for (var e = 0; e < 4; e++)
            {
                var (a, b) = (Math.Min(c[e], c[(e + 1) % 4]), Math.Max(c[e], c[(e + 1) % 4]));
                if (a == b) continue;
                if (!first.TryAdd((a, b), q) && faceOf[first[(a, b)]] != faceOf[q])
                    pairs.Add((Math.Min(faceOf[first[(a, b)]], faceOf[q]), Math.Max(faceOf[first[(a, b)]], faceOf[q])));
            }
        }
        return pairs;
    }

    static string Key(Mesh mesh, int a, int b, int c)
    {
        var k = new[] { Bits(mesh.Vertices[a]), Bits(mesh.Vertices[b]), Bits(mesh.Vertices[c]) }.Order().ToArray();
        return string.Join(";", k);
    }

    static (long, long, long) Bits(Point3D p) => (BitConverter.DoubleToInt64Bits(p.X), BitConverter.DoubleToInt64Bits(p.Y), BitConverter.DoubleToInt64Bits(p.Z));

    internal static double SignedVolume(Mesh mesh)
    {
        var v = 0.0;
        foreach (var t in mesh.Triangles())
        {
            var (a, b, c) = (mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C]);
            v += (a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X)) / 6;
        }
        return v;
    }
}
