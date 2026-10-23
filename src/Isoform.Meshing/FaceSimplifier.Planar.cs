using BasAutomation.Geometry;

namespace Isoform.Meshing;

public static partial class FaceSimplifier
{
    // A vertex this close to the line through its two neighbours on an edge is on it: vertices lie on both planes to
    // ~1e-15, so this only keeps the corners.
    const double Straight = 1e-9;

    sealed partial class Work
    {
        /// <summary>
        /// The vertices on a straight edge between two planar faces that are not corners: each on its two faces only,
        /// passed once by a loop of each (the two loops running opposite ways through it), on the line through its
        /// neighbours and between them. They go from both faces at once.
        /// </summary>
        public bool[] Removable(bool[] planar, List<int[]>?[] loops, bool[] kept)
        {
            var (seen, prev, next, first) = (new int[V.Length], new int[V.Length], new int[V.Length], new int[V.Length]);
            for (var f = 0; f < FaceCount; f++)
            {
                if (!planar[f] || kept[f]) continue;
                foreach (var loop in loops[f]!)
                    for (var i = 0; i < loop.Length; i++)
                    {
                        var (v, p, n) = (loop[i], loop[(i + loop.Length - 1) % loop.Length], loop[(i + 1) % loop.Length]);
                        if (seen[v] == 0) (seen[v], prev[v], next[v], first[v]) = (1, p, n, f);
                        else seen[v] = seen[v] == 1 && first[v] != f && prev[v] == n && next[v] == p ? 2 : 3;
                    }
            }
            var removable = new bool[V.Length];
            for (var v = 0; v < V.Length; v++)
            {
                if (seen[v] != 2 || FacesOf(v).Length != 2) continue;
                var (u, w) = (V[prev[v]], V[next[v]]);
                var along = w - u;
                var (s, length) = ((V[v] - u).Dot(along), along.LengthSquared);
                removable[v] = s > 0 && s < length && (V[v] - u).Cross(along).Length <= Straight * Math.Sqrt(length);
            }
            return removable;
        }

        /// <summary>
        /// A planar face from its boundary loops alone, less the removable vertices, triangulated in its plane; null when
        /// that fails.
        /// </summary>
        public int[]? Triangulate(int face, List<int[]> loops, bool[] removable, bool[] planar)
        {
            var s = SurfaceOf(face);
            var n = s.Axis * Side(face, s, _field);
            // u × v = n: seen from outside, outer loops turn anticlockwise.
            var e = Math.Abs(n.X) <= Math.Abs(n.Y) && Math.Abs(n.X) <= Math.Abs(n.Z) ? new Vector3D(1, 0, 0)
                : Math.Abs(n.Y) <= Math.Abs(n.Z) ? new Vector3D(0, 1, 0) : new Vector3D(0, 0, 1);
            var u = (e - n * n.Dot(e)).Normalized();
            var v = n.Cross(u);
            var o = V[loops[0][0]];
            var points = new List<int>();
            var index = new Dictionary<int, int>();
            var reduced = new List<int[]>();
            foreach (var loop in loops)
            {
                var kept = new List<int>();
                foreach (var p in loop)
                {
                    if (removable[p]) continue;
                    if (!index.TryGetValue(p, out var i))
                    {
                        index.Add(p, i = points.Count);
                        points.Add(p);
                    }
                    kept.Add(i);
                }
                reduced.Add([.. kept]);
            }
            var (x, y) = (new double[points.Count], new double[points.Count]);
            for (var i = 0; i < points.Count; i++)
            {
                var d = V[points[i]] - o;
                (x[i], y[i]) = (d.Dot(u), d.Dot(v));
            }
            // No diagonal along an edge another face has, or another planar face may draw too.
            bool Apart(int i, int j)
            {
                var (a, b) = (points[i], points[j]);
                foreach (var g in FacesOf(a))
                    if (g != face && FacesOf(b).Contains(g) && (planar[g] || WithEdge(g, a, b) >= 0 || WithEdge(g, b, a) >= 0)) return true;
                return false;
            }
            if (PolygonTriangulation.Triangulate(x, y, reduced, Straight, Apart) is not { } triangles) return null;
            for (var i = 0; i < triangles.Length; i++) triangles[i] = points[triangles[i]];
            return triangles;
        }
    }
}
