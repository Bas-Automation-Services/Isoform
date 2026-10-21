using BasAutomation.Geometry;
using Isoform.Geometry;
using Isoform.Meshing;

namespace Isoform.Modeling;

/// <summary>
/// Derives result topology from the owner-labelled mesh on every regeneration. Identity comes from the sources;
/// the mesh only says which owner regions are connected. Adjacent regions on geometrically identical surfaces
/// (e.g. two coplanar tops) become one face that keeps both sources. Works on flat arrays: a vertex→quads
/// adjacency instead of an edge dictionary. A triangle is a quad whose last corner repeats.
/// </summary>
public static class TopologyBuilder
{
    /// <param name="Pinches">
    /// Mesh edges that more than two quads share, as (midpoint, owners round it): one vertex there stands for surface
    /// pieces the cells could not tell apart, so the mesh is not a closed manifold there.
    /// </param>
    /// <param name="FaceOwners">Per face, the surfaces its polygons belong to.</param>
    /// <param name="EdgePoints">Per edge, the mesh vertices of its chain.</param>
    public sealed record Result(Topology Topology, int[] FaceOfQuad, IReadOnlyList<(Point3D At, int[] Owners)> Pinches,
        IReadOnlyList<int[]> FaceOwners, IReadOnlyList<Point3D[]> EdgePoints);

    /// <param name="surfaces">Best <see cref="Oriented"/>, so a face of a cut matches the coincident face it touches.</param>
    /// <param name="field">The body's field: with it, an edge between two planes that meet the part in one point collapses.</param>
    public static Result Build(Mesh mesh, IReadOnlyList<Surface> surfaces, double tolerance, Field? field = null)
    {
        // Plain arrays: in WebAssembly, arrays of structs read through IReadOnlyList are many times slower.
        var quads = mesh.Quads.ToArray();
        var vertices = mesh.Vertices.ToArray();
        var quadCount = quads.Length;

        // Owners on the same geometric surface share a class.
        var classes = new UnionFind(surfaces.Count);
        for (var i = 0; i < surfaces.Count; i++)
        for (var j = i + 1; j < surfaces.Count; j++)
            if (surfaces[i].SameGeometry(surfaces[j], tolerance)) classes.Union(i, j);
        var surfaceClass = Enumerable.Range(0, surfaces.Count).Select(classes.Find).ToArray();

        var incident = new Incidence(quads, vertices.Length);

        // Neighbour quads across each of the four edges (one per edge on a manifold mesh, more otherwise).
        var bodies = new UnionFind(quadCount);
        var faces = new UnionFind(quadCount);
        Span<int> around = stackalloc int[16];
        for (var q = 0; q < quadCount; q++)
            for (var e = 0; e < 4; e++)
            {
                var (a, b) = Edge(quads[q], e);
                if (a == b) continue; // a triangle's repeated corner
                var n = incident.Sharing(a, b, around);
                for (var k = 0; k < n; k++)
                {
                    var r = around[k];
                    if (r <= q) continue;
                    bodies.Union(q, r);
                    if (surfaceClass[quads[q].Owner] == surfaceClass[quads[r].Owner]) faces.Union(q, r);
                }
            }

        var (faceIndex, faceCount) = Number(quadCount, faces);
        var faceOfQuad = new int[quadCount];
        for (var q = 0; q < quadCount; q++) faceOfQuad[q] = faceIndex[faces.Find(q)];
        var bodyOfQuad = Bodies(vertices, quads, bodies);

        var faceOwners = Enumerable.Range(0, faceCount).Select(_ => new HashSet<int>()).ToArray();
        var faceBody = new int[faceCount];
        for (var q = 0; q < quadCount; q++)
        {
            faceOwners[faceOfQuad[q]].Add(quads[q].Owner);
            faceBody[faceOfQuad[q]] = bodyOfQuad.Index[q];
        }
        var topoFaces = Enumerable.Range(0, faceCount).Select(f => new TopoFace(f,
            faceOwners[f].Select(o => surfaces[o].Source).Distinct().Order(StringComparer.Ordinal).ToList(), faceBody[f])).ToList();

        // Edges: connected chains of mesh edges separating two faces. Each mesh edge is visited once, from the
        // lowest-numbered quad around it.
        var segments = new List<Segment>();
        var pinches = new List<(Point3D, int[])>();
        for (var q = 0; q < quadCount; q++)
            for (var e = 0; e < 4; e++)
            {
                var (a, b) = Edge(quads[q], e);
                if (a == b) continue;
                var n = incident.Sharing(a, b, around);
                var lowest = true;
                var mixed = false;
                for (var k = 0; k < n; k++)
                {
                    lowest &= around[k] >= q;
                    mixed |= faceOfQuad[around[k]] != faceOfQuad[q];
                }
                if (lowest && n > 2)
                {
                    var (va, vb) = (vertices[a], vertices[b]);
                    pinches.Add((new Point3D((va.X + vb.X) / 2, (va.Y + vb.Y) / 2, (va.Z + vb.Z) / 2), [.. around[..n].ToArray().Select(r => quads[r].Owner).Distinct().Order()]));
                }
                if (!lowest || !mixed) continue;
                var fs = new List<int>(n);
                for (var k = 0; k < n; k++)
                    if (!fs.Contains(faceOfQuad[around[k]])) fs.Add(faceOfQuad[around[k]]);
                fs.Sort();
                for (var i = 0; i < fs.Count; i++)
                for (var j = i + 1; j < fs.Count; j++)
                    segments.Add(new(fs[i], fs[j], Math.Min(a, b), Math.Max(a, b)));
            }
        // Integer comparisons throughout: tuple comparers and keys are slow in WebAssembly.
        static int Compare(int a, int b) => a < b ? -1 : a > b ? 1 : 0;
        segments.Sort((x, y) => x.V1 != y.V1 ? Compare(x.V1, y.V1) : x.V2 != y.V2 ? Compare(x.V2, y.V2) : x.A != y.A ? Compare(x.A, y.A) : Compare(x.B, y.B));
        // Segments between the same two faces that share an end are one chain: sort their ends and join neighbours.
        var chains = new UnionFind(segments.Count);
        var ends = new int[2 * segments.Count];
        for (var k = 0; k < ends.Length; k++) ends[k] = k;
        int End(int k) => k % 2 == 0 ? segments[k / 2].V1 : segments[k / 2].V2;
        Array.Sort(ends, (x, y) =>
        {
            var (s, t) = (segments[x / 2], segments[y / 2]);
            return s.A != t.A ? Compare(s.A, t.A) : s.B != t.B ? Compare(s.B, t.B) : Compare(End(x), End(y));
        });
        for (var k = 1; k < ends.Length; k++)
        {
            var (s, t) = (segments[ends[k - 1] / 2], segments[ends[k] / 2]);
            if (s.A == t.A && s.B == t.B && End(ends[k - 1]) == End(ends[k])) chains.Union(ends[k - 1] / 2, ends[k] / 2);
        }
        var (chainIndex, _) = Number(segments.Count, chains);
        // A chain between two planes whose common line meets the part in one point is no edge: the cells joined the
        // faces across a sliver of a third one there (a cut whose trace touches a face at a vertex). It collapses to
        // that vertex.
        var collapsed = new Dictionary<int, List<int>>(); // chain root → its mesh vertices
        var chainVertices = Enumerable.Range(0, segments.Count).GroupBy(chains.Find)
            .ToDictionary(g => g.Key, g => g.SelectMany(k => new[] { segments[k].V1, segments[k].V2 }).Distinct().ToList());
        Surface? Plane(HashSet<int> owners) => owners.All(o => surfaces[o].Kind == SurfaceKind.Plane) ? surfaces[owners.Min()] : null;
        if (field is not null)
            foreach (var (root, near) in chainVertices)
            {
                var (a, b) = (Plane(faceOwners[segments[root].A]), Plane(faceOwners[segments[root].B]));
                if (a is not null && b is not null && PointContact(field, a, b, near.Select(v => vertices[v]).ToList(), tolerance)) collapsed[root] = near;
            }
        // A chain's root is its smallest segment: numbered in that order.
        var topoEdges = new List<TopoEdge>();
        var edgePoints = new List<Point3D[]>();
        for (var i = 0; i < segments.Count; i++)
            if (chainIndex[i] >= 0 && !collapsed.ContainsKey(i))
            {
                topoEdges.Add(new TopoEdge(topoEdges.Count, segments[i].A, segments[i].B));
                edgePoints.Add([.. chainVertices[i].Select(v => vertices[v])]);
            }

        // Vertices: clusters of mesh vertices where three or more faces meet.
        var corners = new List<(int Vertex, List<int> Faces)>();
        for (var v = 0; v < vertices.Length; v++)
        {
            var quadsHere = incident.QuadsOf(v);
            var first = faceOfQuad[quadsHere[0]];
            var single = true;
            foreach (var q in quadsHere) single &= faceOfQuad[q] == first;
            if (single) continue;
            var fs = new List<int>();
            foreach (var q in quadsHere)
                if (!fs.Contains(faceOfQuad[q])) fs.Add(faceOfQuad[q]);
            if (fs.Count < 3) continue;
            fs.Sort();
            corners.Add((v, fs));
        }
        // Neighbouring corner vertices are one vertex when one's faces include the other's (a corner spread over
        // several cells) or when they coincide (a vertex of four or more faces). Different corners closer than a
        // cell, such as the top and bottom of a thin plate's edge, stay apart.
        var cornerSlot = corners.Select((c, i) => (c.Vertex, i)).ToDictionary(x => x.Vertex, x => x.i);
        var clusters = new UnionFind(corners.Count);
        foreach (var (vertex, slot) in cornerSlot)
            foreach (var q in incident.QuadsOf(vertex))
                for (var e = 0; e < 4; e++)
                {
                    var (a, b) = Edge(quads[q], e);
                    if (a != b && (a == vertex || b == vertex) && cornerSlot.TryGetValue(a == vertex ? b : a, out var other)
                        && SameCorner(corners[slot], corners[other]))
                        clusters.Union(slot, other);
                }

        foreach (var chain in collapsed.Values)
        {
            var slots = chain.Where(cornerSlot.ContainsKey).Select(v => cornerSlot[v]).ToList();
            foreach (var slot in slots.Skip(1)) clusters.Union(slots[0], slot);
        }

        bool SameCorner((int Vertex, List<int> Faces) x, (int Vertex, List<int> Faces) y) =>
            x.Faces.All(y.Faces.Contains) || y.Faces.All(x.Faces.Contains)
            || (vertices[x.Vertex] - vertices[y.Vertex]).Length <= 10 * tolerance;
        var (clusterIndex, _) = Number(corners.Count, clusters);
        var topoVertices = corners
            .Select((c, i) => (Cluster: clusterIndex[clusters.Find(i)], c.Faces))
            .GroupBy(x => x.Cluster)
            .OrderBy(g => g.Key)
            .Select(g => new TopoVertex(g.Key, g.SelectMany(x => x.Faces).Distinct().Order().ToList()))
            .ToList();

        return new Result(new Topology(topoFaces, topoEdges, topoVertices, bodyOfQuad.Count), faceOfQuad, pinches,
            [.. faceOwners.Select(o => o.Order().ToArray())], edgePoints);
    }

    /// <summary>
    /// The planes' common line touches the part near the chain's vertices in a single point: the lowest field along it
    /// is zero there and has risen ten tolerances away on both sides. A real edge, also between faces coincident within
    /// the tolerance, keeps the field flat along it.
    /// </summary>
    static bool PointContact(Field field, Surface a, Surface b, List<Point3D> near, double tolerance)
    {
        var u = a.Axis.Cross(b.Axis);
        if (u.Length < 1e-6) return false;
        var (ha, hb) = (a.Axis.Dot(a.Origin.AsVector()), b.Axis.Dot(b.Origin.AsVector()));
        var o = (b.Axis.Cross(u) * ha + u.Cross(a.Axis) * hb) / u.LengthSquared;
        var origin = new Point3D(o.X, o.Y, o.Z);
        var d = u.Normalized();
        double F(double t) => field.Value(origin + d * t);

        var ts = near.Select(p => (p - origin).Dot(d)).Order().ToList();
        var reach = near.Max(p => ((p - origin) - d * (p - origin).Dot(d)).Length);
        for (var k = 1; k < ts.Count; k++) reach = Math.Max(reach, ts[k] - ts[k - 1]);
        // Golden section round the lowest vertex: along a line between planes the field is piecewise linear.
        var t0 = ts.MinBy(F);
        var (lo, hi) = (t0 - reach, t0 + reach);
        const double golden = 0.6180339887498949;
        for (var it = 0; it < 80 && hi - lo > tolerance / 100; it++)
        {
            var (x1, x2) = (hi - golden * (hi - lo), lo + golden * (hi - lo));
            if (F(x1) <= F(x2)) hi = x2;
            else lo = x1;
        }
        var t = (lo + hi) / 2;
        var low = F(t);
        // Below zero the line runs through material: a concave edge.
        return Math.Abs(low) <= tolerance && F(t - 10 * tolerance) > low + tolerance / 10 && F(t + 10 * tolerance) > low + tolerance / 10;
    }

    /// <summary>
    /// The surfaces as the body's boundary sees them: a plane whose leaf is complemented (a face of a cut) faces the
    /// other way, so it matches a coincident face of the part it touches. A plane in the body both ways, or another
    /// kind, keeps its record.
    /// </summary>
    public static IReadOnlyList<Surface> Oriented(IReadOnlyList<Surface> surfaces, Field body)
    {
        var sense = new Dictionary<int, bool>();
        var both = new HashSet<int>();
        void Walk(Field f, bool negated)
        {
            switch (f)
            {
                case LeafField l:
                    var flipped = l.Complement ^ negated;
                    if (sense.TryGetValue(l.Surface.Id, out var known) && known != flipped) both.Add(l.Surface.Id);
                    sense[l.Surface.Id] = flipped;
                    break;
                case NegateField n:
                    Walk(n.Child, !negated);
                    break;
                case MaxField m:
                    foreach (var c in m.Children) Walk(c, negated);
                    break;
                case MinField m:
                    foreach (var c in m.Children) Walk(c, negated);
                    break;
                case ProfileField p:
                    foreach (var l in p.Leaves) Walk(l, negated);
                    break;
                case RoundedEdgeField r:
                    Walk(r.A, negated);
                    Walk(r.B, negated);
                    break;
                case LoopEdgeField l:
                    Walk(l.Cap, negated);
                    Walk(l.Side, negated);
                    break;
                case BlendField b:
                    Walk(b.A, negated);
                    Walk(b.B, negated);
                    break;
                case ShellField s:
                    Walk(s.Outer, negated);
                    break;
            }
        }
        Walk(body, false);
        return surfaces.Select(s => s.Kind == SurfaceKind.Plane && sense.GetValueOrDefault(s.Id) && !both.Contains(s.Id) ? s with { Axis = -s.Axis } : s).ToList();
    }

    /// <summary>Vertex → quads adjacency in compressed rows.</summary>
    sealed class Incidence
    {
        readonly int[] _start;
        readonly int[] _quads;
        readonly Quad[] _all;

        public Incidence(Quad[] quads, int vertexCount)
        {
            _all = quads;
            _start = new int[vertexCount + 1];
            foreach (var q in _all)
            {
                _start[q.A + 1]++;
                _start[q.B + 1]++;
                _start[q.C + 1]++;
                if (!q.IsTriangle) _start[q.D + 1]++;
            }
            for (var v = 0; v < vertexCount; v++) _start[v + 1] += _start[v];
            _quads = new int[_start[^1]];
            var cursor = (int[])_start.Clone();
            for (var q = 0; q < _all.Length; q++)
            {
                _quads[cursor[_all[q].A]++] = q;
                _quads[cursor[_all[q].B]++] = q;
                _quads[cursor[_all[q].C]++] = q;
                if (!_all[q].IsTriangle) _quads[cursor[_all[q].D]++] = q;
            }
        }

        public ReadOnlySpan<int> QuadsOf(int v) => _quads.AsSpan(_start[v], _start[v + 1] - _start[v]);

        /// <summary>Writes the quads that have the mesh edge (a, b) into <paramref name="into"/>; returns how many.</summary>
        public int Sharing(int a, int b, Span<int> into)
        {
            var n = 0;
            for (var k = _start[a]; k < _start[a + 1]; k++)
                if (HasEdge(_all[_quads[k]], a, b)) into[n++] = _quads[k];
            return n;
        }

        static bool HasEdge(Quad q, int a, int b) =>
            Same(q.A, q.B, a, b) || Same(q.B, q.C, a, b) || Same(q.C, q.D, a, b) || Same(q.D, q.A, a, b);

        static bool Same(int x, int y, int a, int b) => (x == a && y == b) || (x == b && y == a);
    }

    /// <summary>
    /// Surface components with positive enclosed volume are bodies; negative ones are voids (a shell's cavity)
    /// and belong to the smallest body whose bounds contain them.
    /// </summary>
    static (int[] Index, int Count) Bodies(Point3D[] v, Quad[] quads, UnionFind components)
    {
        var (numbering, n) = Number(quads.Length, components);
        var volume = new double[n];
        var lo = Enumerable.Repeat(new Point3D(double.MaxValue, double.MaxValue, double.MaxValue), n).ToArray();
        var hi = Enumerable.Repeat(new Point3D(double.MinValue, double.MinValue, double.MinValue), n).ToArray();
        var component = new int[quads.Length];
        for (var q = 0; q < quads.Length; q++)
        {
            var c = component[q] = numbering[components.Find(q)];
            var quad = quads[q];
            var (a, b, cc, d) = (v[quad.A].AsVector(), v[quad.B].AsVector(), v[quad.C].AsVector(), v[quad.D].AsVector());
            volume[c] += (a.Dot(b.Cross(cc)) + a.Dot(cc.Cross(d))) / 6;
            for (var corner = 0; corner < 4; corner++)
            {
                var p = v[corner switch { 0 => quad.A, 1 => quad.B, 2 => quad.C, _ => quad.D }];
                lo[c] = new(Math.Min(lo[c].X, p.X), Math.Min(lo[c].Y, p.Y), Math.Min(lo[c].Z, p.Z));
                hi[c] = new(Math.Max(hi[c].X, p.X), Math.Max(hi[c].Y, p.Y), Math.Max(hi[c].Z, p.Z));
            }
        }

        var body = new int[n];
        var count = 0;
        for (var c = 0; c < n; c++) if (volume[c] > 0) body[c] = count++;
        for (var c = 0; c < n; c++)
        {
            if (volume[c] > 0) continue;
            var host = Enumerable.Range(0, n)
                .Where(b => volume[b] > 0 && lo[b].X <= lo[c].X && lo[b].Y <= lo[c].Y && lo[b].Z <= lo[c].Z && hi[b].X >= hi[c].X && hi[b].Y >= hi[c].Y && hi[b].Z >= hi[c].Z)
                .OrderBy(b => volume[b])
                .Cast<int?>()
                .FirstOrDefault();
            body[c] = host is { } h ? body[h] : count; // an unenclosed inward surface would be an error upstream
        }
        return (component.Select(c => body[c]).ToArray(), count);
    }

    static (int, int) Edge(Quad q, int e) => e switch
    {
        0 => (q.A, q.B),
        1 => (q.B, q.C),
        2 => (q.C, q.D),
        _ => (q.D, q.A),
    };

    readonly record struct Segment(int A, int B, int V1, int V2);

    /// <summary>
    /// Numbers union-find roots 0..n-1 in order of their smallest member, for deterministic indices: Index[root], and
    /// -1 for the other members. A root is its set's smallest member (<see cref="UnionFind.Union"/> keeps the lower).
    /// </summary>
    static (int[] Index, int Count) Number(int count, UnionFind uf)
    {
        var index = new int[count];
        var n = 0;
        for (var i = 0; i < count; i++) index[i] = uf.Find(i) == i ? n++ : -1;
        return (index, n);
    }
}

public sealed class UnionFind
{
    readonly int[] _parent;

    public UnionFind(int n) => _parent = Enumerable.Range(0, n).ToArray();

    public int Find(int x)
    {
        while (_parent[x] != x) x = _parent[x] = _parent[_parent[x]];
        return x;
    }

    public void Union(int a, int b)
    {
        var (ra, rb) = (Find(a), Find(b));
        if (ra != rb) _parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
    }
}
