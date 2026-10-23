namespace Isoform.Meshing;

public static partial class FaceSimplifier
{
    /// <summary>
    /// The boundary loops of every face of a mesh, outer and holes alike: see <see cref="Work.Loops"/>. Null for a
    /// face whose boundary does not close into loops.
    /// </summary>
    public static List<int[]>?[] Loops(Mesh mesh, IReadOnlyList<int> faceOfQuad)
    {
        var m = new Work(mesh, faceOfQuad, [], null!, 0);
        return [.. Enumerable.Range(0, m.FaceCount).Select(m.Loops)];
    }

    sealed partial class Work
    {
        /// <summary>
        /// A face's boundary as closed vertex loops, each in the order of the face's triangles: anticlockwise round the
        /// face seen from outside the part for an outer loop, clockwise for a hole. A boundary edge is one whose face
        /// lies on one side only. At a vertex with several boundary edges (the face pinches to a point there) the loop
        /// goes on with the edge of the same sector of the face round the vertex, so two pieces meeting in a point
        /// give two loops; a loop that still comes back through a vertex is split there, so no loop touches itself.
        /// Null when the boundary does not close (a mesh that is not manifold there).
        /// </summary>
        public List<int[]>? Loops(int face)
        {
            var tris = TrianglesOf(face);
            var visited = new HashSet<long>();
            var loops = new List<int[]>();
            long Key(int a, int b) => (long)a * V.Length + b;
            foreach (var t0 in tris)
                for (var k = 0; k < 3; k++)
                {
                    var (a, b) = (T[3 * t0 + k], T[3 * t0 + (k + 1) % 3]);
                    if (WithEdge(face, b, a) >= 0 || visited.Contains(Key(a, b))) continue;
                    var loop = new List<int>();
                    var (u, v, t) = (a, b, t0);
                    do
                    {
                        if (!visited.Add(Key(u, v)) || loop.Count > 3 * tris.Length) return null;
                        loop.Add(u);
                        // Round v through the face's triangles to the next boundary edge.
                        var w = Next(t, v);
                        for (var turn = 0; WithEdge(face, w, v) is var across and >= 0; turn++)
                        {
                            if (turn > tris.Length) return null;
                            (t, w) = (across, Next(across, v));
                        }
                        (u, v) = (v, w);
                    }
                    while (u != a || v != b);
                    Split(loop, loops);
                }
            return loops;
        }

        /// <summary>A loop split at every vertex it passes twice, into loops that pass each vertex once.</summary>
        static void Split(List<int> loop, List<int[]> into)
        {
            var at = new Dictionary<int, int>();
            var path = new List<int>();
            foreach (var v in loop)
            {
                if (at.TryGetValue(v, out var i))
                {
                    into.Add([.. path.GetRange(i, path.Count - i)]);
                    for (var j = i + 1; j < path.Count; j++) at.Remove(path[j]);
                    path.RemoveRange(i + 1, path.Count - i - 1);
                    continue;
                }
                at[v] = path.Count;
                path.Add(v);
            }
            into.Add([.. path]);
        }
    }
}
