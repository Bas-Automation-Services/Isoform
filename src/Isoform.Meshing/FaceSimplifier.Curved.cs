using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Meshing;

public static partial class FaceSimplifier
{
    // A new triangle turns at most 60° from its surface's normal, so none flips.
    const double MinCos = 0.5;

    // Samples stay within this share of the chord, so the triangle between them stays within all of it.
    const double Sampled = 0.85;

    sealed partial class Work
    {
        /// <summary>
        /// A curved face with fewer interior vertices: each in turn, in vertex order, moves onto the nearest neighbour
        /// it can (a half-edge collapse, so every vertex left is one of the mesh's), until none can. A collapse is taken
        /// when the face stays a manifold patch with the same boundary (the link condition, and no new edge between two
        /// boundary vertices that another face also has) and every new triangle fits the face's surface: its edge
        /// midpoints and centroid within the chord, its normal within 60° of the surface's.
        /// </summary>
        public int[] Collapse(int face)
        {
            var tris = TrianglesOf(face);
            var s = SurfaceOf(face);
            var field = s.Analytic ? _field : Local(face);
            var side = Side(face, s, field);
            var (local, global) = (new Dictionary<int, int>(), new List<int>());
            var c = new int[3 * tris.Length];
            for (var i = 0; i < c.Length; i++)
            {
                var g = T[3 * tris[i / 3] + i % 3];
                if (!local.TryGetValue(g, out var l))
                {
                    local.Add(g, l = global.Count);
                    global.Add(g);
                }
                c[i] = l;
            }
            var n = global.Count;
            var inc = new List<int>[n];
            for (var l = 0; l < n; l++) inc[l] = new List<int>(6);
            for (var i = 0; i < c.Length; i++) inc[c[i]].Add(i / 3);
            var edge = global.Select(OnBoundary).ToArray();
            var dead = new bool[tris.Length];
            var mark = new int[n];
            var stamp = 0;

            bool Has(int t, int v) => c[3 * t] == v || c[3 * t + 1] == v || c[3 * t + 2] == v;
            int Third(int t, int a, int b) => c[3 * t] != a && c[3 * t] != b ? c[3 * t] : c[3 * t + 1] != a && c[3 * t + 1] != b ? c[3 * t + 1] : c[3 * t + 2];
            Point3D P(int t, int k, int a, int b) => V[global[c[3 * t + k] == a ? b : c[3 * t + k]]];

            // Each edge is sampled about every two of the face's own edge lengths (at least at its midpoint, at most 16
            // times): a long triangle is a strip along a straight way of the surface, and strays most across it.
            var spacing = 0.0;
            for (var i = 0; i < c.Length; i++) spacing += (V[global[c[i]]] - V[global[c[i - i % 3 + (i % 3 + 1) % 3]]]).Length;
            spacing = 2 * spacing / c.Length;

            bool Near(Point3D p) => Math.Abs(Measure(s, field, p).Distance) <= Sampled * _chord;

            bool Along(Point3D a, Point3D b)
            {
                var m = Math.Clamp((int)Math.Ceiling((b - a).Length / spacing), 2, 16);
                for (var j = 1; j < m; j++)
                    if (!Near(a + (b - a) * ((double)j / m))) return false;
                return true;
            }

            bool Fits(Point3D p0, Point3D p1, Point3D p2)
            {
                var normal = (p1 - p0).Cross(p2 - p0);
                var length = normal.Length;
                if (!(length > 0)) return false;
                var centroid = new Point3D((p0.X + p1.X + p2.X) / 3, (p0.Y + p1.Y + p2.Y) / 3, (p0.Z + p1.Z + p2.Z) / 3);
                var (d, g) = Measure(s, field, centroid);
                if (!(Math.Abs(d) <= Sampled * _chord)) return false;
                if (!(side * g.Dot(normal) >= MinCos * length)) return false;
                if (!(Along(p0, p1) && Along(p1, p2) && Along(p2, p0))) return false;
                if (!(Near(centroid + (p0 - centroid) / 2) && Near(centroid + (p1 - centroid) / 2) && Near(centroid + (p2 - centroid) / 2))) return false;
                return true;
            }

            bool ShareOther(int a, int b)
            {
                foreach (var f in FacesOf(a))
                    if (f != face && FacesOf(b).Contains(f)) return true;
                return false;
            }

            bool TryCollapse(int a, int b)
            {
                var (shared, x0, x1) = (0, -1, -1);
                foreach (var t in inc[a])
                    if (Has(t, b))
                    {
                        if (shared++ == 0) x0 = Third(t, a, b);
                        else x1 = Third(t, a, b);
                    }
                if (shared != 2) return false;
                stamp++;
                foreach (var t in inc[b])
                    for (var k = 0; k < 3; k++) mark[c[3 * t + k]] = stamp;
                foreach (var t in inc[a])
                {
                    if (Has(t, b)) continue;
                    for (var k = 0; k < 3; k++)
                    {
                        var x = c[3 * t + k];
                        if (x == a || x == x0 || x == x1) continue;
                        // An edge b–x already there, or one another face may draw as well.
                        if (mark[x] == stamp || (edge[b] && edge[x] && ShareOther(global[b], global[x]))) return false;
                    }
                    // (a, x0, x1) would become a triangle that is there already.
                    if (Has(t, x0) && Has(t, x1))
                        foreach (var u in inc[b])
                            if (Has(u, x0) && Has(u, x1)) return false;
                }
                foreach (var t in inc[a])
                    if (!Has(t, b) && !Fits(P(t, 0, a, b), P(t, 1, a, b), P(t, 2, a, b))) return false;

                foreach (var t in inc[a])
                {
                    if (Has(t, b))
                    {
                        dead[t] = true;
                        inc[b].Remove(t);
                        inc[Third(t, a, b)].Remove(t);
                        continue;
                    }
                    for (var k = 0; k < 3; k++)
                        if (c[3 * t + k] == a) c[3 * t + k] = b;
                    inc[b].Add(t);
                }
                inc[a].Clear();
                return true;
            }

            // Every interior vertex once, then again each one whose neighbourhood a collapse changed, in vertex order.
            var (queued, pending) = (new bool[n], new bool[n]);
            var queue = Enumerable.Range(0, n).Where(l => !edge[l]).OrderBy(l => global[l]).ToList();
            foreach (var a in queue) queued[a] = true;
            var near = new List<(double Distance, int Id, int Local)>();
            while (queue.Count > 0)
            {
                var again = new List<int>();
                foreach (var a in queue)
                {
                    queued[a] = false;
                    if (inc[a].Count == 0) continue;
                    near.Clear();
                    stamp++;
                    mark[a] = stamp;
                    foreach (var t in inc[a])
                        for (var k = 0; k < 3; k++)
                        {
                            var b = c[3 * t + k];
                            if (mark[b] == stamp) continue;
                            mark[b] = stamp;
                            near.Add(((V[global[b]] - V[global[a]]).LengthSquared, global[b], b));
                        }
                    near.Sort();
                    foreach (var (_, _, b) in near)
                    {
                        if (!TryCollapse(a, b)) continue;
                        foreach (var t in inc[b])
                            for (var k = 0; k < 3; k++)
                            {
                                var x = c[3 * t + k];
                                if (edge[x] || queued[x] || pending[x]) continue;
                                pending[x] = true;
                                again.Add(x);
                            }
                        break;
                    }
                }
                foreach (var x in again) pending[x] = false;
                queue = [.. again.OrderBy(l => global[l])];
            }

            var result = new List<int>(c.Length);
            for (var t = 0; t < tris.Length; t++)
                if (!dead[t])
                    for (var k = 0; k < 3; k++) result.Add(global[c[3 * t + k]]);
            return [.. result];
        }
    }
}
