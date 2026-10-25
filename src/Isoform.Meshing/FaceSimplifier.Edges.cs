using BasAutomation.Geometry;

namespace Isoform.Meshing;

public static partial class FaceSimplifier
{
    sealed partial class Work
    {
        /// <summary>
        /// The vertices that go from both faces of the edge they are on. An edge is a run of vertices on two faces only,
        /// each passed once by a loop of each face (the two loops running opposite ways through it), between corners; a
        /// run that closes on itself (a hole's rim) is held at three points. Each run keeps the fewest of its vertices
        /// that leave every vertex within the chord of the polyline left (Douglas–Peucker), so a straight edge keeps
        /// only its ends. Faces without loops, and kept faces, lend no vertex.
        /// </summary>
        public bool[] Removable(List<int[]>?[] loops, bool[] kept)
        {
            var (seen, prev, next, first) = (new int[V.Length], new int[V.Length], new int[V.Length], new int[V.Length]);
            for (var f = 0; f < FaceCount; f++)
            {
                if (kept[f] || loops[f] is not { } own) continue;
                foreach (var loop in own)
                    for (var i = 0; i < loop.Length; i++)
                    {
                        var (v, p, n) = (loop[i], loop[(i + loop.Length - 1) % loop.Length], loop[(i + 1) % loop.Length]);
                        if (seen[v] == 0) (seen[v], prev[v], next[v], first[v]) = (1, p, n, f);
                        else seen[v] = seen[v] == 1 && first[v] != f && prev[v] == n && next[v] == p ? 2 : 3;
                    }
            }
            bool OnEdge(int v) => seen[v] == 2 && FacesOf(v).Length == 2;

            var removable = new bool[V.Length];
            var tolerance = Sampled * _chord;
            for (var f = 0; f < FaceCount; f++)
            {
                if (kept[f] || loops[f] is not { } own) continue;
                foreach (var loop in own)
                {
                    var n = loop.Length;
                    // Each run once: from the face with the lower index.
                    bool Mine(int v) => OnEdge(v) && Other(v, f) > f;
                    var start = Array.FindIndex(loop, v => !OnEdge(v));
                    if (start < 0)
                    {
                        if (n < 6 || !Mine(loop[0])) continue;
                        int[] anchors = [0, n / 3, 2 * n / 3, n];
                        for (var k = 0; k < 3; k++) Thin(loop, anchors[k], anchors[k + 1], tolerance, removable);
                        continue;
                    }
                    for (var i = 0; i < n;)
                    {
                        // A run of edge vertices between the corners at start + i and start + j.
                        var j = i + 1;
                        while (j < n && OnEdge(loop[(start + j) % n])) j++;
                        if (j - i > 1 && Mine(loop[(start + i + 1) % n])) Thin(loop, start + i, start + j, tolerance, removable);
                        i = j;
                    }
                }
            }
            return removable;
        }

        /// <summary>The face other than <paramref name="face"/> of a vertex on two faces.</summary>
        int Other(int v, int face) => FacesOf(v)[0] == face ? FacesOf(v)[1] : FacesOf(v)[0];

        /// <summary>
        /// Douglas–Peucker over loop[from..to] (indices taken round the loop): the ends stay, and a vertex goes when every
        /// vertex between its kept neighbours is within <paramref name="tolerance"/> of the segment joining them.
        /// </summary>
        void Thin(int[] loop, int from, int to, double tolerance, bool[] removable)
        {
            int At(int i) => loop[i % loop.Length];
            var spans = new Stack<(int, int)>();
            spans.Push((from, to));
            while (spans.Count > 0)
            {
                var (a, b) = spans.Pop();
                if (b - a < 2) continue;
                var (pa, pb) = (V[At(a)], V[At(b)]);
                var (worst, at) = (-1.0, -1);
                for (var i = a + 1; i < b; i++)
                    if (ToSegment(V[At(i)], pa, pb) is var d && d > worst) (worst, at) = (d, i);
                if (worst <= tolerance)
                    for (var i = a + 1; i < b; i++) removable[At(i)] = true;
                else
                {
                    spans.Push((a, at));
                    spans.Push((at, b));
                }
            }
        }

        static double ToSegment(Point3D p, Point3D a, Point3D b)
        {
            var ab = b - a;
            var length = ab.LengthSquared;
            var t = length > 0 ? Math.Clamp((p - a).Dot(ab) / length, 0, 1) : 0;
            return (p - (a + ab * t)).Length;
        }
    }
}
