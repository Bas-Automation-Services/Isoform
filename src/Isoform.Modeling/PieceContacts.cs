using BasAutomation.Geometry;
using Isoform.Meshing;

namespace Isoform.Modeling;

/// <summary>
/// Pieces of a body that touch within the tolerance. Where a body falls apart into pieces that come closer than the
/// tolerance (two blocks 1e-9 mm apart), the part is one solid at the modeling tolerance but the cells keep it apart.
/// A mesh vertex lies on the exact surface and a flat face's polygons lie in its plane, so a vertex of one piece within
/// the tolerance of another piece's polygons is such a contact; curved faces stray from their polygons by the chord
/// tolerance, far more, so they never count by accident.
/// </summary>
static class PieceContacts
{
    /// <summary>The two faces that touch, and where.</summary>
    public readonly record struct Contact(int FaceA, int FaceB, Point3D At);

    public static Contact? Find(Mesh mesh, IReadOnlyList<int> faceOfQuad, Topology topology, double tolerance)
    {
        var n = topology.BodyCount;
        if (n < 2) return null;
        int Piece(int quad) => topology.Faces[faceOfQuad[quad]].Body;
        var (lo, hi) = (new Point3D[n], new Point3D[n]);
        for (var p = 0; p < n; p++) (lo[p], hi[p]) = (new(double.MaxValue, double.MaxValue, double.MaxValue), new(double.MinValue, double.MinValue, double.MinValue));
        var quadsOf = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        for (var q = 0; q < mesh.Quads.Count; q++)
        {
            var p = Piece(q);
            quadsOf[p].Add(q);
            foreach (var v in Corners(mesh.Quads[q]))
            {
                var x = mesh.Vertices[v];
                (lo[p], hi[p]) = (new(Math.Min(lo[p].X, x.X), Math.Min(lo[p].Y, x.Y), Math.Min(lo[p].Z, x.Z)), new(Math.Max(hi[p].X, x.X), Math.Max(hi[p].Y, x.Y), Math.Max(hi[p].Z, x.Z)));
            }
        }
        bool Within(Point3D x, int p) =>
            x.X >= lo[p].X - tolerance && x.X <= hi[p].X + tolerance && x.Y >= lo[p].Y - tolerance && x.Y <= hi[p].Y + tolerance
            && x.Z >= lo[p].Z - tolerance && x.Z <= hi[p].Z + tolerance;

        // Each piece's polygons, against the vertices of every other piece that lie within its box.
        for (var b = 0; b < n; b++)
        {
            List<(int Quad, Triangle Triangle)>? triangles = null;
            TriangleBvh? bvh = null;
            for (var a = 0; a < n; a++)
            {
                if (a == b || lo[a].X > hi[b].X + tolerance || lo[b].X > hi[a].X + tolerance || lo[a].Y > hi[b].Y + tolerance
                    || lo[b].Y > hi[a].Y + tolerance || lo[a].Z > hi[b].Z + tolerance || lo[b].Z > hi[a].Z + tolerance) continue;
                foreach (var q in quadsOf[a])
                    foreach (var v in Corners(mesh.Quads[q]))
                    {
                        var x = mesh.Vertices[v];
                        if (!Within(x, b)) continue;
                        triangles ??= [.. quadsOf[b].SelectMany(t => Triangles(mesh, t).Select(tr => (t, tr)))];
                        bvh ??= new TriangleBvh(mesh.Vertices, [.. triangles.Select(t => t.Triangle)]);
                        if (bvh.Distance(x) > tolerance) continue;
                        var nearest = triangles.MinBy(t => (TriangleBvh.ClosestOnTriangle(x, mesh.Vertices[t.Triangle.A], mesh.Vertices[t.Triangle.B], mesh.Vertices[t.Triangle.C]) - x).LengthSquared);
                        return new Contact(faceOfQuad[q], faceOfQuad[nearest.Quad], x);
                    }
            }
        }
        return null;
    }

    static int[] Corners(Quad q) => q.IsTriangle ? [q.A, q.B, q.C] : [q.A, q.B, q.C, q.D];

    static IEnumerable<Triangle> Triangles(Mesh mesh, int q) => new Mesh(mesh.Vertices, [mesh.Quads[q]]).Triangles();
}
