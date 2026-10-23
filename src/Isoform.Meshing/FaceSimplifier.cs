using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Meshing;

/// <summary>
/// Fewer triangles for display, with the surfaces as the only judge. Each face is simplified on its own with its
/// boundary shared with its neighbours, so the result stays watertight and every triangle keeps its face:
/// <list type="bullet">
/// <item>a planar face loses its interior vertices and is triangulated from its boundary loops in its plane;</item>
/// <item>vertices along a straight edge between two planar faces go from both faces at once;</item>
/// <item>a curved face loses interior vertices by edge collapse, its boundary locked, while every new triangle stays
/// within the chord of the face's own surface and none flips.</item>
/// </list>
/// The vertices are a subset of the mesh's, so they stay on their surfaces. A face whose result would not close up
/// with its neighbours keeps its own triangles. Deterministic: faces are independent, each simplified in a fixed order.
/// </summary>
public static partial class FaceSimplifier
{
    /// <summary>The simplified mesh, all triangles, and the face of each.</summary>
    /// <param name="faceOfQuad">The face of each quad of <paramref name="mesh"/>.</param>
    /// <param name="surfaces">Every surface by id: analytic ones measure their own distance.</param>
    /// <param name="field">The part's field, for the distance to fillet, blend and lattice surfaces.</param>
    /// <param name="chord">Largest distance a triangle may stray from its face's surface.</param>
    public static (Mesh Mesh, int[] FaceOfTriangle) Simplify(Mesh mesh, IReadOnlyList<int> faceOfQuad, IReadOnlyList<Surface> surfaces, Field field, double chord)
    {
        var m = new Work(mesh, faceOfQuad, surfaces, field, chord);
        var faces = m.FaceCount;
        var result = new int[faces][];
        var planar = new bool[faces];
        var loops = new List<int[]>?[faces];
        Parallel.For(0, faces, f =>
        {
            if (m.Planar(f)) (planar[f], loops[f]) = (true, m.Loops(f));
            else result[f] = m.Collapse(f);
        });

        // Planar faces: vertices on straight edges between two of them go; a face that does not triangulate, or does
        // not close up with its neighbours, keeps its own triangles and locks its boundary for them.
        var kept = new bool[faces];
        for (var f = 0; f < faces; f++)
            if (planar[f] && loops[f] is null) (kept[f], result[f]) = (true, m.Own(f));
        var removable = m.Removable(planar, loops, kept);
        var dirty = Enumerable.Range(0, faces).Where(f => planar[f] && !kept[f]).ToList();
        while (true)
        {
            // Null where a face did not triangulate.
            Parallel.ForEach(dirty, f => result[f] = m.Triangulate(f, loops[f]!, removable, planar)!);
            List<int> reverted = [.. dirty.Where(f => result[f] is null)];
            if (reverted.Count == 0) reverted = m.Open(result, kept);
            if (reverted.Count == 0) break;
            var touched = new SortedSet<int>();
            foreach (var f in reverted)
            {
                (kept[f], result[f]) = (true, m.Own(f));
                if (loops[f] is { } own)
                    foreach (var loop in own)
                        foreach (var v in loop)
                            if (removable[v])
                            {
                                removable[v] = false;
                                foreach (var g in m.FacesOf(v)) touched.Add(g);
                            }
            }
            dirty = [.. touched.Where(g => !kept[g])];
        }
        return m.Assemble(result, mesh.ThinSpots);
    }

    /// <summary>The mesh as triangles, with each vertex's triangles and faces.</summary>
    sealed partial class Work
    {
        public readonly Point3D[] V;
        public readonly int[] T;       // three corners per triangle
        public readonly int[] Face;    // per triangle
        public readonly int[] Owner;   // per triangle
        public readonly int FaceCount;
        readonly int[] _faceStart, _faceTris;   // each face's triangles
        readonly int[] _vertexStart, _vertexTris; // each vertex's triangles
        readonly int[] _vfStart, _vf;           // each vertex's faces
        readonly IReadOnlyList<Surface> _surfaces;
        readonly Field _field;
        readonly double _chord;

        public Work(Mesh mesh, IReadOnlyList<int> faceOfQuad, IReadOnlyList<Surface> surfaces, Field field, double chord)
        {
            (_surfaces, _field, _chord) = (surfaces, field, chord);
            V = mesh.Vertices.ToArray();
            var quads = mesh.Quads.ToArray();
            var count = 0;
            foreach (var q in quads) count += q.IsTriangle ? 1 : 2;
            (T, Face, Owner) = (new int[3 * count], new int[count], new int[count]);
            var t = 0;
            void Add(int a, int b, int c, int face, int owner)
            {
                (T[3 * t], T[3 * t + 1], T[3 * t + 2], Face[t], Owner[t]) = (a, b, c, face, owner);
                t++;
            }
            // Split as Mesh.Triangles does.
            for (var i = 0; i < quads.Length; i++)
            {
                var q = quads[i];
                if (q.IsTriangle) Add(q.A, q.B, q.C, faceOfQuad[i], q.Owner);
                else if ((V[q.A] - V[q.C]).LengthSquared <= (V[q.B] - V[q.D]).LengthSquared)
                {
                    Add(q.A, q.B, q.C, faceOfQuad[i], q.Owner);
                    Add(q.A, q.C, q.D, faceOfQuad[i], q.Owner);
                }
                else
                {
                    Add(q.A, q.B, q.D, faceOfQuad[i], q.Owner);
                    Add(q.B, q.C, q.D, faceOfQuad[i], q.Owner);
                }
            }
            FaceCount = count == 0 ? 0 : Face.Max() + 1;
            (_faceStart, _faceTris) = Group(Face, FaceCount, 1);
            (_vertexStart, _vertexTris) = Group(T, V.Length, 3);
            _vfStart = new int[V.Length + 1];
            var vf = new List<int>();
            for (var v = 0; v < V.Length; v++)
            {
                _vfStart[v] = vf.Count;
                for (var k = _vertexStart[v]; k < _vertexStart[v + 1]; k++)
                {
                    var f = Face[_vertexTris[k]];
                    var seen = false;
                    for (var j = _vfStart[v]; j < vf.Count && !seen; j++) seen = vf[j] == f;
                    if (!seen) vf.Add(f);
                }
            }
            _vfStart[V.Length] = vf.Count;
            _vf = [.. vf];
        }

        /// <summary>Items grouped by key, in order: <paramref name="per"/> keys per item.</summary>
        static (int[] Start, int[] Items) Group(int[] keys, int groups, int per)
        {
            var start = new int[groups + 1];
            foreach (var k in keys) start[k + 1]++;
            for (var g = 0; g < groups; g++) start[g + 1] += start[g];
            var items = new int[keys.Length];
            var at = start[..^1];
            for (var i = 0; i < keys.Length; i++) items[at[keys[i]]++] = i / per;
            return (start, items);
        }

        public ReadOnlySpan<int> TrianglesOf(int face) => _faceTris.AsSpan(_faceStart[face], _faceStart[face + 1] - _faceStart[face]);
        public ReadOnlySpan<int> TrianglesAt(int v) => _vertexTris.AsSpan(_vertexStart[v], _vertexStart[v + 1] - _vertexStart[v]);
        public ReadOnlySpan<int> FacesOf(int v) => _vf.AsSpan(_vfStart[v], _vfStart[v + 1] - _vfStart[v]);
        public bool OnBoundary(int v) => _vfStart[v + 1] - _vfStart[v] > 1;

        /// <summary>The corner after <paramref name="v"/> in triangle <paramref name="t"/>.</summary>
        public int Next(int t, int v) => T[3 * t] == v ? T[3 * t + 1] : T[3 * t + 1] == v ? T[3 * t + 2] : T[3 * t];

        /// <summary>The face's triangle with the directed edge a → b, or -1.</summary>
        public int WithEdge(int face, int a, int b)
        {
            foreach (var t in TrianglesAt(a))
                if (Face[t] == face && Next(t, a) == b) return t;
            return -1;
        }

        /// <summary>The surface a face is measured against: its first triangle's (a face's owners share their geometry).</summary>
        public Surface SurfaceOf(int face) => _surfaces[Owner[TrianglesOf(face)[0]]];

        /// <summary>The face's surface is a plane and every vertex of the face lies on it within the chord.</summary>
        public bool Planar(int face)
        {
            var s = SurfaceOf(face);
            if (s.Kind != SurfaceKind.Plane) return false;
            foreach (var t in TrianglesOf(face))
                for (var k = 0; k < 3; k++)
                    if (Math.Abs(s.Distance(V[T[3 * t + k]])) > _chord) return false;
            return true;
        }

        /// <summary>Signed distance to a surface and its unit normal, for analytic and field surfaces alike.</summary>
        public static (double Distance, Vector3D Normal) Measure(Surface s, Field field, Point3D p)
        {
            if (s.Analytic) return (s.Distance(p), s.Gradient(p));
            var sample = field.SampleSurface(s.Id, p)!.Value;
            var length = sample.Gradient.Length;
            return (sample.Value / length, sample.Gradient / length);
        }

        /// <summary>The field simplified for the box round a face's vertices: the same there, and quicker to sample.</summary>
        public Field Local(int face)
        {
            var (lo, hi) = (new Point3D(double.MaxValue, double.MaxValue, double.MaxValue), new Point3D(double.MinValue, double.MinValue, double.MinValue));
            foreach (var t in TrianglesOf(face))
                for (var k = 0; k < 3; k++)
                {
                    var p = V[T[3 * t + k]];
                    (lo, hi) = (new Point3D(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z)), new Point3D(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z)));
                }
            var pad = new Vector3D(1e-6, 1e-6, 1e-6);
            return _field.Simplify(new Box3D(lo - pad, hi + pad));
        }

        /// <summary>
        /// +1 when the surface's normal points out of the part on this face, by its triangles' winding, else -1: a cut's
        /// surface and a shell's inner one face the other way.
        /// </summary>
        public double Side(int face, Surface s, Field field)
        {
            var sum = 0.0;
            foreach (var t in TrianglesOf(face))
            {
                var (a, b, c) = (V[T[3 * t]], V[T[3 * t + 1]], V[T[3 * t + 2]]);
                var centroid = new Point3D((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
                sum += Measure(s, field, centroid).Normal.Dot((b - a).Cross(c - a));
            }
            return sum < 0 ? -1 : 1;
        }

        /// <summary>The face's own triangles.</summary>
        public int[] Own(int face)
        {
            var tris = TrianglesOf(face);
            var corners = new int[3 * tris.Length];
            for (var i = 0; i < tris.Length; i++)
                for (var k = 0; k < 3; k++) corners[3 * i + k] = T[3 * tris[i] + k];
            return corners;
        }

        /// <summary>
        /// The faces whose new triangles leave an edge that is not used exactly once each way: edges of kept faces only
        /// are the mesh's own and left as they are.
        /// </summary>
        public List<int> Open(int[][] result, bool[] kept)
        {
            var directed = new Dictionary<long, int>();
            long Key(int a, int b) => (long)a * V.Length + b;
            foreach (var c in result)
                for (var i = 0; i < c.Length; i += 3)
                    for (var k = 0; k < 3; k++)
                    {
                        var key = Key(c[i + k], c[i + (k + 1) % 3]);
                        directed[key] = directed.GetValueOrDefault(key) + 1;
                    }
            bool Bad(int a, int b) => directed.GetValueOrDefault(Key(a, b)) != 1 || directed.GetValueOrDefault(Key(b, a)) != 1;
            var open = new List<int>();
            for (var f = 0; f < result.Length; f++)
            {
                if (kept[f]) continue;
                var c = result[f];
                for (var i = 0; i < c.Length; i += 3)
                    if (Bad(c[i], c[i + 1]) || Bad(c[i + 1], c[i + 2]) || Bad(c[i + 2], c[i]))
                    {
                        open.Add(f);
                        break;
                    }
            }
            return open;
        }

        /// <summary>One mesh of triangles from every face's, with only the vertices still used, in their order.</summary>
        public (Mesh, int[]) Assemble(int[][] result, IReadOnlyList<ThinSpot> spots)
        {
            var index = new int[V.Length];
            Array.Fill(index, -1);
            foreach (var c in result)
                foreach (var v in c) index[v] = 0;
            var vertices = new List<Point3D>();
            for (var v = 0; v < V.Length; v++)
                if (index[v] == 0)
                {
                    index[v] = vertices.Count;
                    vertices.Add(V[v]);
                }
            var (quads, faces) = (new List<Quad>(), new List<int>());
            for (var f = 0; f < result.Length; f++)
            {
                var owner = Owner[TrianglesOf(f)[0]];
                var c = result[f];
                for (var i = 0; i < c.Length; i += 3)
                {
                    quads.Add(new Quad(index[c[i]], index[c[i + 1]], index[c[i + 2]], index[c[i + 2]], owner));
                    faces.Add(f);
                }
            }
            return (new Mesh(vertices, quads, spots), [.. faces]);
        }
    }
}
