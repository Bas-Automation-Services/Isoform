using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Interactive;
using Isoform.Modeling;

namespace Isoform.Tests.Interactive;

/// <summary>
/// T5: the page shows the simplified mesh, a drag preview the octree mesh as it is; triangles keep their faces, so a
/// pick on one resolves through the octree mesh to the face it shows.
/// </summary>
public class DisplayMeshTests
{
    static readonly ModelingSettings Settings = new() { CellSize = 0.5, ChordTolerance = 0.05 };

    static HashSet<(int, int)> Pairs(MeshBuffers b) => [.. b.LineFaces.Chunk(2).Select(p => (p[0], p[1]))];

    static int OctreeTriangles(View view) => view.Mesh!.Quads.Sum(q => q.IsTriangle ? 1 : 2);

    [Fact]
    public void A_regeneration_shows_fewer_triangles_and_a_preview_shows_them_all()
    {
        var document = CanonicalFormat.Write(TapeTests.Twenty);
        var session = new Session();

        var main = session.Regenerate(document, "[]", Settings);
        var preview = session.Regenerate(document, "[]", Settings, Session.Preview);

        main.Buffers.Triangles.Should().BeLessThan(OctreeTriangles(main.View!) / 3);
        preview.Buffers.Triangles.Should().Be(OctreeTriangles(preview.View!));
        Pairs(main.Buffers).Should().BeEquivalentTo(Pairs(preview.Buffers), "the same faces meet");
    }

    [Fact]
    public void A_pick_on_a_shown_triangle_resolves_to_its_face()
    {
        var session = new Session();
        var r = session.Regenerate(CanonicalFormat.Write(TapeTests.Twenty), "[]", Settings);
        var (view, b) = (r.View!, r.Buffers);

        for (var t = 0; t < b.Triangles; t += 7)
        {
            var p = Enumerable.Range(0, 3).Select(k => new Point3D(b.Positions[9 * t + 3 * k], b.Positions[9 * t + 3 * k + 1], b.Positions[9 * t + 3 * k + 2])).ToArray();
            var c = new Point3D((p[0].X + p[1].X + p[2].X) / 3, (p[0].Y + p[1].Y + p[2].Y) / 3, (p[0].Z + p[1].Z + p[2].Z) / 3);
            var face = b.Faces[t];
            var source = view.Faces[face][0];
            var picked = view.Part!.FaceNear(source, c.X, c.Y, c.Z);
            picked.Should().NotBeNull();
            view.Faces[picked!.Value].Should().Contain(source);
            if (view.Faces.Count(f => f.Contains(source)) == 1) picked.Should().Be(face);
        }
    }

    [Fact]
    public void An_assembly_shows_every_instance_simplified_in_place()
    {
        var (_, bundle) = Examples.Bundles[0];
        var session = new Session();
        var r = session.Regenerate(CanonicalFormat.Write(bundle), "[]", Settings);
        var preview = session.Regenerate(CanonicalFormat.Write(bundle), "[]", Settings, Session.Preview);

        r.Buffers.Triangles.Should().BeLessThan(OctreeTriangles(r.View!));
        r.Buffers.Faces.Distinct().Order().Should().Equal(Enumerable.Range(0, r.View!.Faces.Count));
        Pairs(r.Buffers).Should().BeEquivalentTo(Pairs(preview.Buffers));
        // Placed where the octree mesh is: the same box.
        static (double, double) Range(double[] p, int axis) => (p.Where((_, i) => i % 3 == axis).Min(), p.Where((_, i) => i % 3 == axis).Max());
        for (var axis = 0; axis < 3; axis++)
        {
            var (a, b) = (Range(r.Buffers.Positions, axis), Range(preview.Buffers.Positions, axis));
            a.Item1.Should().BeApproximately(b.Item1, 0.05);
            a.Item2.Should().BeApproximately(b.Item2, 0.05);
        }
    }
}
