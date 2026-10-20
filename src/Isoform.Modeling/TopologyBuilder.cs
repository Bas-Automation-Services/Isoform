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
    public sealed record Result(Topology Topology, int[] FaceOfQuad);

    public static Result Build(Mesh mesh, IReadOnlyList<Surface> surfaces, double tolerance)
    {
        var quads = mesh.Quads;
        var quadCount = quads.Count;

        // Owners on the same geometric surface share a class.
        var classes = new UnionFind(surfaces.Count);
        for (var i = 0; i < surfaces.Count; i++)
        for (var j = i + 1; j < surfaces.Count; j++)
            if (surfaces[i].SameGeometry(surfaces[j], tolerance)) classes.Union(i, j);
        var surfaceClass = Enumerable.Range(0, surfaces.Count).Select(classes.Find).ToArray();

        var incident = new Incidence(mesh);

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

        var faceIndex = Number(quadCount, faces);
        var faceOfQuad = new int[quadCount];
        for (var q = 0; q < quadCount; q++) faceOfQuad[q] = faceIndex[faces.Find(q)];
        var bodyOfQuad = Bodies(mesh, bodies);

        var faceCount = faceIndex.Count;
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
        var segments = new List<(int A, int B, int V1, int V2)>();
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
                if (!lowest || !mixed) continue;
                var fs = new List<int>(n);
                for (var k = 0; k < n; k++)
                    if (!fs.Contains(faceOfQuad[around[k]])) fs.Add(faceOfQuad[around[k]]);
                fs.Sort();
                for (var i = 0; i < fs.Count; i++)
                for (var j = i + 1; j < fs.Count; j++)
                    segments.Add((fs[i], fs[j], Math.Min(a, b), Math.Max(a, b)));
            }
        segments.Sort((x, y) => (x.V1, x.V2, x.A, x.B).CompareTo((y.V1, y.V2, y.A, y.B)));
        var chains = new UnionFind(segments.Count);
        foreach (var group in segments.Select((s, i) => (s, i)).GroupBy(x => (x.s.A, x.s.B)))
        {
            var byVertex = new Dictionary<int, int>();
            foreach (var (s, i) in group)
                foreach (var v in new[] { s.V1, s.V2 })
                    if (byVertex.TryGetValue(v, out var other)) chains.Union(i, other); else byVertex[v] = i;
        }
        var chainIndex = Number(segments.Count, chains);
        var topoEdges = chainIndex
            .OrderBy(kv => kv.Value)
            .Select(kv => new TopoEdge(kv.Value, segments[kv.Key].A, segments[kv.Key].B))
            .ToList();

        // Vertices: clusters of mesh vertices where three or more faces meet.
        var corners = new List<(int Vertex, List<int> Faces)>();
        for (var v = 0; v < mesh.Vertices.Count; v++)
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
            foreach (var q in incident.QuadsOf(vertex).ToArray())
                foreach (var (a, b) in Edges(quads[q]))
                    if (a != b && (a == vertex || b == vertex) && cornerSlot.TryGetValue(a == vertex ? b : a, out var other)
                        && SameCorner(corners[slot], corners[other]))
                        clusters.Union(slot, other);

        bool SameCorner((int Vertex, List<int> Faces) x, (int Vertex, List<int> Faces) y) =>
            x.Faces.All(y.Faces.Contains) || y.Faces.All(x.Faces.Contains)
            || (mesh.Vertices[x.Vertex] - mesh.Vertices[y.Vertex]).Length <= 10 * tolerance;
        var clusterIndex = Number(corners.Count, clusters);
        var topoVertices = corners
            .Select((c, i) => (Cluster: clusterIndex[clusters.Find(i)], c.Faces))
            .GroupBy(x => x.Cluster)
            .OrderBy(g => g.Key)
            .Select(g => new TopoVertex(g.Key, g.SelectMany(x => x.Faces).Distinct().Order().ToList()))
            .ToList();

        return new Result(new Topology(topoFaces, topoEdges, topoVertices, bodyOfQuad.Count), faceOfQuad);
    }

    /// <summary>Vertex → quads adjacency in compressed rows.</summary>
    sealed class Incidence
    {
        readonly int[] _start;
        readonly int[] _quads;
        readonly IReadOnlyList<Quad> _all;

        public Incidence(Mesh mesh)
        {
            _all = mesh.Quads;
            _start = new int[mesh.Vertices.Count + 1];
            foreach (var q in _all)
            {
                _start[q.A + 1]++;
                _start[q.B + 1]++;
                _start[q.C + 1]++;
                if (!q.IsTriangle) _start[q.D + 1]++;
            }
            for (var v = 0; v < mesh.Vertices.Count; v++) _start[v + 1] += _start[v];
            _quads = new int[_start[^1]];
            var cursor = (int[])_start.Clone();
            for (var q = 0; q < _all.Count; q++)
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
    static (int[] Index, int Count) Bodies(Mesh mesh, UnionFind components)
    {
        var numbering = Number(mesh.Quads.Count, components);
        var n = numbering.Count;
        var volume = new double[n];
        var lo = Enumerable.Repeat(new Point3D(double.MaxValue, double.MaxValue, double.MaxValue), n).ToArray();
        var hi = Enumerable.Repeat(new Point3D(double.MinValue, double.MinValue, double.MinValue), n).ToArray();
        var component = new int[mesh.Quads.Count];
        var v = mesh.Vertices;
        for (var q = 0; q < mesh.Quads.Count; q++)
        {
            var c = component[q] = numbering[components.Find(q)];
            var quad = mesh.Quads[q];
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

    static IEnumerable<(int, int)> Edges(Quad q)
    {
        yield return (q.A, q.B);
        yield return (q.B, q.C);
        yield return (q.C, q.D);
        yield return (q.D, q.A);
    }

    /// <summary>Numbers union-find roots 0..n-1 in order of their smallest member, for deterministic indices.</summary>
    static Dictionary<int, int> Number(int count, UnionFind uf)
    {
        var index = new Dictionary<int, int>();
        for (var i = 0; i < count; i++)
        {
            var root = uf.Find(i);
            if (!index.ContainsKey(root)) index[root] = index.Count;
        }
        return index;
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
