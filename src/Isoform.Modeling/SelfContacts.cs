using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Modeling;

/// <summary>
/// A piece of a body that touches itself: one of its corners on one of its edges, away from that edge's ends, where the
/// edge's two surfaces are not both the corner's. On a
/// manifold solid a corner lies only on the edges that end in it; one on another edge is where two stretches of the
/// solid meet in a point (a sweep's bend running through the corner where it starts), a contact of zero thickness no
/// face can be bounded at. The corner is put exactly onto its faces' surfaces and counts where it lies on the edge's two
/// within the tolerance, as the exact B-rep checks it. Corners and edges of analytic surfaces only; pieces that touch
/// each other are <see cref="PieceContacts"/>'.
/// </summary>
static class SelfContacts
{
    /// <summary>The corner, the edge it lies on, and where.</summary>
    public readonly record struct Contact(int Vertex, int Edge, Point3D At);

    /// <param name="reach">How far the mesh may put a corner or an edge's chain from the exact one: two cells.</param>
    public static Contact? Find(TopologyBuilder.Result built, IReadOnlyList<Surface> surfaces, double tolerance, double reach)
    {
        var t = built.Topology;
        bool On(int face, Point3D p) => built.FaceOwners[face].Any(o => surfaces[o].Analytic && Math.Abs(surfaces[o].Distance(p)) <= tolerance);
        bool Near(IEnumerable<Point3D> points, Point3D p) => points.Any(x => (x - p).Length <= reach);
        // A face of the corner or one on the same surface: a face a hole or a knob splits in two runs on along the same
        // edge curve as its other half, which then passes through the corner as well.
        bool Of(IReadOnlyList<int> corner, int face) =>
            corner.Any(f => f == face || surfaces[built.FaceOwners[f][0]].SameGeometry(surfaces[built.FaceOwners[face][0]], tolerance));
        for (var v = 0; v < t.Vertices.Count; v++)
        {
            var faces = t.Vertices[v].Faces;
            if (Corner(faces.Select(f => built.FaceOwners[f]).ToList(), surfaces, built.VertexPoints[v][0], tolerance, reach) is not { } c) continue;
            for (var e = 0; e < t.Edges.Count; e++)
            {
                var (a, b) = (t.Edges[e].FaceA, t.Edges[e].FaceB);
                if (t.Faces[a].Body != t.Faces[faces[0]].Body || Of(faces, a) && Of(faces, b) || !On(a, c) || !On(b, c) || !Near(built.EdgePoints[e], c)) continue;
                // At one of the edge's own ends: a corner of four faces or more that the cells split in two.
                if (Enumerable.Range(0, t.Vertices.Count).Any(w => Of(t.Vertices[w].Faces, a) && Of(t.Vertices[w].Faces, b) && Near(built.VertexPoints[w], c))) continue;
                return new Contact(v, e, c);
            }
        }
        return null;
    }

    /// <summary>
    /// The exact corner near <paramref name="p"/> on a surface of every face, by Newton steps on the three whose normals
    /// are the most independent; null where a surface is not analytic, the normals are (nearly) dependent or it does not
    /// converge within reach.
    /// </summary>
    static Point3D? Corner(List<int[]> faces, IReadOnlyList<Surface> surfaces, Point3D p, double tolerance, double reach)
    {
        if (faces.Any(f => f.Any(o => !surfaces[o].Analytic))) return null;
        var on = faces.Select(f => surfaces[f.MinBy(o => Math.Abs(surfaces[o].Distance(p)))]).ToList();
        var (best, pick) = (0.0, (0, 0, 0));
        for (var i = 0; i < on.Count; i++)
        for (var j = i + 1; j < on.Count; j++)
        for (var k = j + 1; k < on.Count; k++)
            if (Math.Abs(on[i].Gradient(p).Dot(on[j].Gradient(p).Cross(on[k].Gradient(p)))) is var d && d > best) (best, pick) = (d, (i, j, k));
        if (best < 1e-3) return null;
        var (s1, s2, s3) = (on[pick.Item1], on[pick.Item2], on[pick.Item3]);
        var x = p;
        for (var it = 0; it < 20; it++)
        {
            var (g1, g2, g3) = (s1.Gradient(x), s2.Gradient(x), s3.Gradient(x));
            var det = g1.Dot(g2.Cross(g3));
            if (Math.Abs(det) < 1e-6) return null;
            var step = (g2.Cross(g3) * s1.Distance(x) + g3.Cross(g1) * s2.Distance(x) + g1.Cross(g2) * s3.Distance(x)) / det;
            x -= step;
            if (step.Length <= tolerance / 100) break;
        }
        return (x - p).Length <= reach && on.All(s => Math.Abs(s.Distance(x)) <= tolerance) ? x : null;
    }
}
