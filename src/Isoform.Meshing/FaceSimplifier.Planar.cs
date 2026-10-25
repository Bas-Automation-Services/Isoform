using BasAutomation.Geometry;

namespace Isoform.Meshing;

public static partial class FaceSimplifier
{
    // Points this close to the line through two others are on it, for the triangulation.
    const double Straight = 1e-9;

    sealed partial class Work
    {
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
