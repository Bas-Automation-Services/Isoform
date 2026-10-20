using BasAutomation.Geometry;
using Isoform.Geometry;
using Isoform.Meshing;

namespace Isoform.Modeling;

/// <summary>
/// Derives result topology from the owner-labelled mesh on every regeneration. Identity comes from the sources;
/// the mesh only says which owner regions are connected. Adjacent regions on geometrically identical surfaces
/// (e.g. two coplanar tops) become one face that keeps both sources.
/// </summary>
public static class TopologyBuilder
{
    public sealed record Result(Topology Topology, int[] FaceOfQuad);

    public static Result Build(Mesh mesh, IReadOnlyList<Surface> surfaces, double tolerance)
    {
        var quads = mesh.Quads;

        // Owners on the same geometric surface share a class.
        var surfaceClass = new UnionFind(surfaces.Count);
        for (var i = 0; i < surfaces.Count; i++)
        for (var j = i + 1; j < surfaces.Count; j++)
            if (surfaces[i].SameGeometry(surfaces[j], tolerance)) surfaceClass.Union(i, j);

        var edgeQuads = new Dictionary<(int, int), List<int>>();
        for (var q = 0; q < quads.Count; q++)
            foreach (var e in Edges(quads[q]))
            {
                if (!edgeQuads.TryGetValue(e, out var list)) edgeQuads[e] = list = [];
                list.Add(q);
            }

        var bodies = new UnionFind(quads.Count);
        var faces = new UnionFind(quads.Count);
        foreach (var list in edgeQuads.Values)
            for (var i = 1; i < list.Count; i++)
            {
                bodies.Union(list[0], list[i]);
                if (surfaceClass.Find(quads[list[0]].Owner) == surfaceClass.Find(quads[list[i]].Owner))
                    faces.Union(list[0], list[i]);
            }

        var bodyIndex = Bodies(mesh, bodies, out var bodyCount);
        var faceIndex = Number(quads.Count, faces);
        var faceOfQuad = Enumerable.Range(0, quads.Count).Select(q => faceIndex[faces.Find(q)]).ToArray();

        var faceCount = faceIndex.Count;
        var faceSources = new SortedSet<string>[faceCount];
        var faceBody = new int[faceCount];
        for (var q = 0; q < quads.Count; q++)
        {
            var f = faceOfQuad[q];
            (faceSources[f] ??= new SortedSet<string>(StringComparer.Ordinal)).Add(surfaces[quads[q].Owner].Source);
            faceBody[f] = bodyIndex[bodies.Find(q)];
        }
        var topoFaces = Enumerable.Range(0, faceCount)
            .Select(f => new TopoFace(f, faceSources[f].ToList(), faceBody[f])).ToList();

        // Edges: connected chains of mesh edges separating two faces.
        var segments = new List<(int A, int B, int V1, int V2)>();
        foreach (var (e, list) in edgeQuads.OrderBy(kv => kv.Key))
        {
            var fs = list.Select(q => faceOfQuad[q]).Distinct().Order().ToList();
            for (var i = 0; i < fs.Count; i++)
            for (var j = i + 1; j < fs.Count; j++)
                segments.Add((fs[i], fs[j], e.Item1, e.Item2));
        }
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
        var vertexFaces = new Dictionary<int, SortedSet<int>>();
        for (var q = 0; q < quads.Count; q++)
            foreach (var v in new[] { quads[q].A, quads[q].B, quads[q].C, quads[q].D })
                (vertexFaces.TryGetValue(v, out var set) ? set : vertexFaces[v] = []).Add(faceOfQuad[q]);
        var corners = vertexFaces.Where(kv => kv.Value.Count >= 3).Select(kv => kv.Key).Order().ToList();
        var cornerSlot = corners.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
        var clusters = new UnionFind(corners.Count);
        foreach (var e in edgeQuads.Keys)
            if (cornerSlot.TryGetValue(e.Item1, out var c1) && cornerSlot.TryGetValue(e.Item2, out var c2))
                clusters.Union(c1, c2);
        var clusterIndex = Number(corners.Count, clusters);
        var topoVertices = corners
            .Select((v, i) => (Cluster: clusterIndex[clusters.Find(i)], Faces: vertexFaces[v]))
            .GroupBy(x => x.Cluster)
            .OrderBy(g => g.Key)
            .Select(g => new TopoVertex(g.Key, g.SelectMany(x => x.Faces).Distinct().Order().ToList()))
            .ToList();

        return new Result(new Topology(topoFaces, topoEdges, topoVertices, bodyCount), faceOfQuad);
    }

    /// <summary>
    /// Surface components with positive enclosed volume are bodies; negative ones are voids (a shell's cavity)
    /// and belong to the smallest body whose bounds contain them.
    /// </summary>
    static Dictionary<int, int> Bodies(Mesh mesh, UnionFind components, out int bodyCount)
    {
        var volume = new Dictionary<int, double>();
        var lo = new Dictionary<int, Point3D>();
        var hi = new Dictionary<int, Point3D>();
        var v = mesh.Vertices;
        for (var q = 0; q < mesh.Quads.Count; q++)
        {
            var root = components.Find(q);
            var quad = mesh.Quads[q];
            var (a, b, c, d) = (v[quad.A].AsVector(), v[quad.B].AsVector(), v[quad.C].AsVector(), v[quad.D].AsVector());
            volume[root] = volume.GetValueOrDefault(root) + (a.Dot(b.Cross(c)) + a.Dot(c.Cross(d))) / 6;
            foreach (var p in new[] { v[quad.A], v[quad.B], v[quad.C], v[quad.D] })
            {
                lo[root] = lo.TryGetValue(root, out var l) ? new(Math.Min(l.X, p.X), Math.Min(l.Y, p.Y), Math.Min(l.Z, p.Z)) : p;
                hi[root] = hi.TryGetValue(root, out var h) ? new(Math.Max(h.X, p.X), Math.Max(h.Y, p.Y), Math.Max(h.Z, p.Z)) : p;
            }
        }

        var roots = Number(mesh.Quads.Count, components).OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        var index = new Dictionary<int, int>();
        foreach (var r in roots.Where(r => volume[r] > 0)) index[r] = index.Count;
        bodyCount = index.Count;
        foreach (var r in roots.Where(r => volume[r] <= 0))
        {
            var host = index.Keys
                .Where(b => lo[b].X <= lo[r].X && lo[b].Y <= lo[r].Y && lo[b].Z <= lo[r].Z && hi[b].X >= hi[r].X && hi[b].Y >= hi[r].Y && hi[b].Z >= hi[r].Z)
                .OrderBy(b => volume[b])
                .Cast<int?>()
                .FirstOrDefault();
            index[r] = host is { } h ? index[h] : index.Count; // an unenclosed inward surface would be an error upstream
        }
        return index;
    }

    static IEnumerable<(int, int)> Edges(Quad q)
    {
        yield return Key(q.A, q.B);
        yield return Key(q.B, q.C);
        yield return Key(q.C, q.D);
        yield return Key(q.D, q.A);
    }

    static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

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
