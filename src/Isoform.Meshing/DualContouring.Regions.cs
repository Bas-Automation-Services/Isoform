using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Meshing;

public static partial class DualContouring
{
    /// <summary>
    /// Plans a mesh as regions: the octree to region level, as <see cref="Build"/> builds it, and a content key per
    /// region. With <paramref name="snapGrid"/> the lattice sits on a fixed world grid (offset off round numbers),
    /// so an edit that moves the part's bounding box keeps the unchanged regions' keys. The mesh is then that of
    /// <c>Build(snapGrid: true)</c>: another lattice than the unsnapped one, so another mesh, as deterministic.
    /// </summary>
    /// <param name="refine">
    /// Boxes whose finest cell is smaller than <paramref name="cellSize"/>: a power of two smaller (rounded down to
    /// one). The lattice is then refined to the smallest of them; outside the boxes the mesh is the one without them.
    /// </param>
    public static MeshPlan Plan(Field field, Point3D min, Point3D max, double cellSize, double chordTolerance, bool snapGrid = false,
        IReadOnlyList<(Box3D Box, double Cell)>? refine = null)
    {
        var coarse = Grid.Around(min, max, cellSize, snapGrid);
        static int Levels(double ratio) => Math.Max(0, (int)Math.Ceiling(Math.Log2(ratio) - 1e-9));
        var levels = refine is null or { Count: 0 } ? 0 : refine.Max(z => Levels(cellSize / z.Cell));
        var grid = coarse.Refined(levels);
        Zone[]? zones = null;
        if (levels > 0)
        {
            int At(double x, double o, int offset, bool up) => (int)(up ? Math.Ceiling((x - o) / grid.H) : Math.Floor((x - o) / grid.H)) - offset;
            zones = [.. refine!.Select(z => new Zone(
                At(z.Box.Min.X, grid.Origin.X, grid.Offset.I, false), At(z.Box.Min.Y, grid.Origin.Y, grid.Offset.J, false), At(z.Box.Min.Z, grid.Origin.Z, grid.Offset.K, false),
                At(z.Box.Max.X, grid.Origin.X, grid.Offset.I, true), At(z.Box.Max.Y, grid.Origin.Y, grid.Offset.J, true), At(z.Box.Max.Z, grid.Origin.Z, grid.Offset.K, true),
                1 << (levels - Levels(cellSize / z.Cell))))];
        }
        var ctx = new Context(grid, Prune: true, Adaptive: true, chordTolerance, UnitDepth: grid.UnitDepth, Coarse: 1 << levels, Zones: zones);
        var root = new Node(0, 0, 0, grid.RootSize);
        Subdivide(root, field, ctx, 0);
        var units = new List<Node>();
        Units(root, units);
        // What a neighbour's job reads of a region: its children, certified empty or simplified, as Split makes them.
        var split = new (bool Empty, Field? Field)[units.Count][];
        Parallel.For(0, units.Count, i => split[i] = units[i].Pending is null ? [] : Children(units[i], grid));
        var children = new Dictionary<Node, (bool Empty, Field? Field)[]>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < units.Count; i++) children[units[i]] = split[i];
        var regions = new MeshRegion[units.Count];
        Parallel.For(0, units.Count, i => regions[i] = Region(i, field, ctx, root, units[i], children));
        return new MeshPlan(field, ctx with { UnitDepth = -1 }, root, regions);
    }

    /// <summary>A region of the plan: pending, or a leaf that holds surface (once split, a region is still pending).</summary>
    static bool IsRegion(Node n) => n.Pending is not null || n.Children is null && n.Field is not null;

    static void Units(Node n, List<Node> units)
    {
        if (IsRegion(n)) units.Add(n);
        else if (n.Children is { } ch)
            foreach (var c in ch) Units(c, units);
    }

    static void Touching(Node n, Node u, List<Node> units)
    {
        if (!Touches(n, u)) return;
        if (IsRegion(n))
        {
            if (n != u) units.Add(n);
        }
        else if (n.Children is { } ch)
            foreach (var c in ch) Touching(c, u, units);
    }

    static (bool Empty, Field? Field)[] Children(Node u, Grid g)
    {
        var result = new (bool, Field?)[8];
        var half = u.Size / 2;
        for (var c = 0; c < 8; c++)
        {
            var child = new Node(u.I + (c & 1) * half, u.J + (c >> 1 & 1) * half, u.K + (c >> 2) * half, half);
            var box = g.Box(child);
            var bounds = u.Pending!.Bounds(box);
            var empty = child.I >= g.Nx || child.J >= g.Ny || child.K >= g.Nz || bounds.Lo > CertifyMargin || bounds.Hi < -CertifyMargin;
            result[c] = (empty, empty ? null : u.Pending.Simplify(box));
        }
        return result;
    }

    /// <summary>
    /// The key hashes all a region's job reads: the lattice and where it ends nearby, the region and its field, the
    /// field its vertices use, and of each neighbour touching it what the job builds from: a leaf's field, or the
    /// children of a pending neighbour that touch the region. An edit deep inside a neighbour leaves the key alone.
    /// </summary>
    static MeshRegion Region(int index, Field field, Context ctx, Node root, Node u, Dictionary<Node, (bool Empty, Field? Field)[]> children)
    {
        var g = ctx.Grid;
        var near = field.Simplify(g.Box((u.I, u.J, u.K, u.Size), 1));
        var neighbours = new List<Node>();
        Touching(root, u, neighbours);
        var reach = neighbours.Count == 0 ? 0 : neighbours.Max(n => n.Size);

        var f = new Fingerprint();
        f.Add(3); // format version
        f.Add(g.H);
        f.Add(ctx.ChordTolerance);
        f.Add(g.Origin);
        f.Add(g.Offset.I + u.I);
        f.Add(g.Offset.J + u.J);
        f.Add(g.Offset.K + u.K);
        f.Add(u.Size);
        foreach (var end in new[] { g.Nx - u.I, g.Ny - u.J, g.Nz - u.K }) f.Add(Math.Clamp(end, -reach, u.Size + reach));
        // Refinement: the coarse cell and the zones the region or its neighbours reach, clipped to that reach.
        f.Add(ctx.Coarse);
        foreach (var z in ctx.Zones ?? [])
        {
            if (!z.Overlaps(u.I, u.J, u.K, u.Size, reach)) continue;
            int Clip(int x, int lo) => Math.Clamp(x - lo, -reach, u.Size + reach);
            f.Add(Clip(z.I0, u.I));
            f.Add(Clip(z.J0, u.J));
            f.Add(Clip(z.K0, u.K));
            f.Add(Clip(z.I1, u.I));
            f.Add(Clip(z.J1, u.J));
            f.Add(Clip(z.K1, u.K));
            f.Add(z.Unit);
        }
        f.Add(u.Pending is null);
        f.Add(u.Pending ?? u.Field);
        f.Add(near);
        f.Add(neighbours.Count);
        foreach (var n in neighbours)
        {
            f.Add(n.I - u.I);
            f.Add(n.J - u.J);
            f.Add(n.K - u.K);
            f.Add(n.Size);
            if (n.Pending is null)
            {
                f.Add(n.Field);
                continue;
            }
            var half = n.Size / 2;
            for (var c = 0; c < 8; c++)
            {
                var child = new Node(n.I + (c & 1) * half, n.J + (c >> 1 & 1) * half, n.K + (c >> 2) * half, half);
                if (!Touches(child, u)) continue;
                f.Add(c);
                f.Add(children[n][c].Empty);
                f.Add(children[n][c].Field);
            }
        }
        return new MeshRegion(index, u, near, f.Hash, [.. f.Surfaces]);
    }

    /// <summary>Builds a pending region's whole subtree in place, for its own job and its neighbours' to share.</summary>
    internal static void Expand(MeshPlan plan, MeshRegion region)
    {
        var u = region.Node;
        if (u.Pending is { } field && u.Children is null) Split(u, field, plan.Context with { Serial = true }, 0);
    }

    /// <summary>
    /// A region's job. The traversal finds every edge at the region, in the global order; the leaves round them come
    /// from the region's subtree and its neighbours', shared where <see cref="Expand"/> built them and otherwise built
    /// here where they touch the region, exactly as the whole octree builds them. The region computes each crossing
    /// it needs (also those of the neighbours' edges at its side, bit-identically), the vertices of its own leaves
    /// and the polygons of its edges.
    /// </summary>
    internal static RegionMesh MeshRegion(MeshPlan plan, MeshRegion region)
    {
        var u = region.Node;
        var ctx = plan.Context with { Region = u, Serial = true };
        var grid = ctx.Grid;
        Node? own = null;
        var root = View(plan.Root, u, ctx, 0, ref own);
        var hits = new List<Hit>();
        CellProc(root, hits, ctx, 0);

        var leaves = new List<Node>();
        var ids = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        int Own(Node n)
        {
            if (!Within(n, u)) return -1;
            if (!ids.TryGetValue(n, out var id))
            {
                ids[n] = id = leaves.Count;
                leaves.Add(n);
            }
            return id;
        }

        var crossings = new List<Crossing>(hits.Count);
        var at = new List<int>(hits.Count); // the hit of each crossing
        var pairs = new List<long>(hits.Count * 4);
        var owned = new List<(int E, int A, int B, int C, int D)>(hits.Count); // crossings whose polygon this region owns
        for (var h = 0; h < hits.Count; h++)
        {
            var hit = hits[h];
            var (a, b, c, d) = (hit.N0, hit.N1, hit.N2, hit.N3);
            var (ia, ib, ic, id) = (Own(a), Own(b), Own(c), Own(d));
            if (ia < 0 && ib < 0 && ic < 0 && id < 0) continue;
            var e = crossings.Count;
            var f = hit.Smallest.Field!;
            var point = FindRoot(f, hit.P0, hit.P1);
            var sample = f.Sample(point);
            crossings.Add(new(point, sample.Gradient.Normalized(), sample.Owner));
            at.Add(h);
            // As in Build: a leaf filling two quadrants takes the crossing once.
            if (ia >= 0) pairs.Add(((long)ia << 32) | (uint)e);
            if (ib >= 0 && b != a) pairs.Add(((long)ib << 32) | (uint)e);
            if (ic >= 0 && c != a && c != b) pairs.Add(((long)ic << 32) | (uint)e);
            if (id >= 0 && d != a && d != b && d != c) pairs.Add(((long)id << 32) | (uint)e);
            if (ic >= 0) owned.Add((e, ia, ib, ic, id)); // the (+,+) quadrant's region owns the polygon
        }

        pairs.Sort();
        var (oi, oj, ok) = grid.Offset;
        var cells = new List<int>(leaves.Count * 4);
        var positions = new List<double>(leaves.Count * 3);
        var cacheable = true;
        var spots = new ConcurrentBag<ThinSpot>();
        // The pieces of the finest leaves round the polygons, the neighbours' too: each from the leaf's own field, as
        // Build finds them.
        var pieces = new sbyte[]?[leaves.Count];
        var others = new Dictionary<Node, sbyte[]?>(ReferenceEqualityComparer.Instance);
        Span<Crossing> buffer = stackalloc Crossing[48];
        for (var start = 0; start < pairs.Count;)
        {
            var leaf = (int)(pairs[start] >> 32);
            var end = start;
            while (end < pairs.Count && (int)(pairs[end] >> 32) == leaf) end++;
            var n = end - start;
            Span<Crossing> local = n <= 48 ? buffer : new Crossing[n];
            var node = leaves[leaf];
            var table = pieces[leaf] = node.Multi && n >= 6 ? Pieces(node, grid) : null;
            for (var piece = 0; piece < PieceCount(table); piece++)
            {
                var m = 0;
                for (var k = start; k < end; k++)
                {
                    var e = (int)(uint)pairs[k];
                    if (table is null || table[LocalEdge(node, hits[at[e]])] == piece) local[m++] = crossings[e];
                }
                var near = node.Field!;
                if (!SingleOwner(local[..m]))
                {
                    near = Near(plan.Field, grid, node, region.Near, out var inside);
                    cacheable &= inside;
                }
                var p = CellVertex(near, plan.Field, grid, node, local[..m], spots, out var wide);
                cacheable &= !wide;
                cells.Add(oi + node.I);
                cells.Add(oj + node.J);
                cells.Add(ok + node.K);
                cells.Add(piece);
                positions.Add(p.X);
                positions.Add(p.Y);
                positions.Add(p.Z);
            }
            start = end;
        }

        var polygons = new List<int>(owned.Count * RegionMesh.PolygonInts);
        void Corner(Node n, int id, in Hit hit)
        {
            polygons.Add(oi + n.I);
            polygons.Add(oj + n.J);
            polygons.Add(ok + n.K);
            var table = !n.Multi ? null : id >= 0 ? pieces[id] : others.TryGetValue(n, out var t) ? t : others[n] = Pieces(n, grid);
            polygons.Add(table is null ? 0 : table[LocalEdge(n, hit)]);
        }
        foreach (var (e, ia, ib, ic, id) in owned)
        {
            var hit = hits[at[e]];
            polygons.Add(oi + hit.I);
            polygons.Add(oj + hit.J);
            polygons.Add(ok + hit.K);
            polygons.Add(hit.Axis);
            polygons.Add(hit.Smallest.Size);
            Corner(hit.N0, ia, hit);
            Corner(hit.N1, ib, hit);
            Corner(hit.N2, ic, hit);
            Corner(hit.N3, id, hit);
            polygons.Add(region.Local[crossings[e].Owner]);
            polygons.Add(hit.StartInside ? 1 : 0);
        }

        // Vertices in leaf order, which is Build's (depth-first is Morton order of the leaves' corners), then by piece:
        // the region's leaves are one depth-first block, so the assembly only concatenates the regions.
        var keys = new ulong[cells.Count / 4];
        var order = new int[keys.Length];
        for (var k = 0; k < keys.Length; k++) (keys[k], order[k]) = (VertexKey(cells[4 * k] - oi, cells[4 * k + 1] - oj, cells[4 * k + 2] - ok, cells[4 * k + 3]), k);
        Array.Sort(keys, order);

        var surface = new List<Node>();
        Surfaced(own!, surface);
        foreach (var leaf in surface.Where(l => l.Multi)) FindThin(leaf, grid, spots);
        var thin = spots.ToList();
        // Leaves round no crossing have no vertex: the field may still take the other sign inside them.
        var pockets = surface.Where(l => !ids.ContainsKey(l)).Select(l => FindPocket(l, grid)).OfType<Pocket>().ToList();
        return new RegionMesh
        {
            Cells = [.. order.SelectMany(k => new[] { cells[4 * k], cells[4 * k + 1], cells[4 * k + 2], cells[4 * k + 3] })],
            Positions = [.. order.SelectMany(k => new[] { positions[3 * k], positions[3 * k + 1], positions[3 * k + 2] })],
            Polygons = [.. polygons],
            ThinOwners = [.. thin.SelectMany(s => new[] { region.Local[s.OwnerA], region.Local[s.OwnerB] })],
            Thin = [.. thin.SelectMany(s => new[] { s.Location.X, s.Location.Y, s.Location.Z, s.Thickness })],
            Pockets = [.. pockets.SelectMany(p => new[] { p.Location.X, p.Location.Y, p.Location.Z, p.Value, p.Corner })],
            Cacheable = cacheable,
        };
    }

    /// <summary>
    /// The plan's tree as a job sees it. Nodes away from the region are left as they are: the traversal does not enter
    /// them. A pending region not yet built is built here where it touches the region; the nodes above the regions
    /// are copied to point at it. <paramref name="own"/> gets the region's own node.
    /// </summary>
    static Node View(Node n, Node u, Context ctx, int depth, ref Node? own)
    {
        if (!Touches(n, u)) return n;
        Node view;
        if (n.Pending is { } field && n.Children is null)
        {
            view = new Node(n.I, n.J, n.K, n.Size);
            Split(view, field, ctx, depth);
        }
        else if (IsRegion(n) || n.Children is null) view = n; // built, a leaf or empty: shared as it is
        else
        {
            view = new Node(n.I, n.J, n.K, n.Size) { Children = new Node[8] };
            for (var c = 0; c < 8; c++) view.Children[c] = View(n.Children[c], u, ctx, depth + 1, ref own);
        }
        if (n == u) own = view;
        return view;
    }

    /// <summary>The leaves holding surface.</summary>
    static void Surfaced(Node n, List<Node> into)
    {
        if (n.Children is { } ch)
            foreach (var c in ch) Surfaced(c, into);
        else if (n.Field is not null) into.Add(n);
    }

    internal static Mesh Assemble(MeshPlan plan, IReadOnlyList<RegionMesh> results)
    {
        var grid = plan.Context.Grid;
        var (oi, oj, ok) = grid.Offset;
        ulong Cell(int[] a, int at) => VertexKey(a[at] - oi, a[at + 1] - oj, a[at + 2] - ok, a[at + 3]);

        // Vertices in leaf order, as Build numbers them: the regions are in depth-first order and each has its
        // vertices in Morton order, the depth-first order of the leaves' corners. Sorted only if a cached result came
        // from a plan with another root, which could break that.
        var keys = new ulong[results.Sum(r => r.VertexCount)];
        var positions = new Point3D[keys.Length];
        var v = 0;
        foreach (var r in results)
            for (var k = 0; k < r.VertexCount; k++, v++)
                (keys[v], positions[v]) = (Cell(r.Cells, 4 * k), new Point3D(r.Positions[3 * k], r.Positions[3 * k + 1], r.Positions[3 * k + 2]));
        if (!Ascending(keys))
        {
            var byKey = new int[keys.Length];
            for (var k = 0; k < byKey.Length; k++) byKey[k] = k;
            Array.Sort(keys, byKey);
            positions = byKey.Select(k => positions[k]).ToArray();
        }
        var index = new Dictionary<ulong, int>(keys.Length);
        for (var k = 0; k < keys.Length; k++) index[keys[k]] = k;

        // Polygons in the traversal's order, owners renumbered from the regions' keys to this plan's ids. Each
        // region's come in that order already, so the runs are merged.
        var order = new UInt128[results.Sum(r => r.Polygons.Length / RegionMesh.PolygonInts)];
        var quads = new Quad[order.Length];
        var runs = new List<int> { 0 };
        var q = 0;
        for (var ri = 0; ri < results.Count; ri++)
        {
            var (p, surfaces) = (results[ri].Polygons, plan.Regions[ri].Surfaces);
            for (var at = 0; at < p.Length; at += RegionMesh.PolygonInts, q++)
            {
                order[q] = TraversalKey(p[at] - oi, p[at + 1] - oj, p[at + 2] - ok, p[at + 3], p[at + 4], grid.RootSize);
                quads[q] = Polygon(index[Cell(p, at + 5)], index[Cell(p, at + 9)], index[Cell(p, at + 13)], index[Cell(p, at + 17)],
                    p[at + 22] == 1, surfaces[p[at + 21]]);
            }
            if (q > runs[^1]) runs.Add(q);
        }
        quads = [.. Merge(order, runs).Select(k => quads[k])];

        var spots = new List<ThinSpot>();
        for (var ri = 0; ri < results.Count; ri++)
        {
            var (r, surfaces) = (results[ri], plan.Regions[ri].Surfaces);
            for (var s = 0; s < r.ThinOwners.Length / 2; s++)
            {
                var (a, b) = (surfaces[r.ThinOwners[2 * s]], surfaces[r.ThinOwners[2 * s + 1]]);
                spots.Add(new ThinSpot(Math.Min(a, b), Math.Max(a, b), new Point3D(r.Thin[4 * s], r.Thin[4 * s + 1], r.Thin[4 * s + 2]), r.Thin[4 * s + 3]));
            }
        }
        var pockets = results.SelectMany(r => Enumerable.Range(0, r.Pockets.Length / 5)
            .Select(s => new Pocket(new Point3D(r.Pockets[5 * s], r.Pockets[5 * s + 1], r.Pockets[5 * s + 2]), r.Pockets[5 * s + 3], r.Pockets[5 * s + 4])));
        return new Mesh(positions, quads, Sorted(spots), Sorted(pockets));
    }

    /// <summary>A vertex by its leaf's corner in Morton order (2²⁰ cells a side), then its piece (a cube holds at most four).</summary>
    static ulong VertexKey(int i, int j, int k, int piece) => Morton(i, j, k) << 2 | (uint)piece;

    static bool Ascending(ulong[] keys)
    {
        for (var k = 1; k < keys.Length; k++)
            if (keys[k] < keys[k - 1]) return false;
        return true;
    }

    /// <summary>
    /// The order of <paramref name="keys"/>, whose runs between <paramref name="bounds"/> are each ascending: merged
    /// pairwise. A run out of order (a cached result planned under another root) falls back to a full sort.
    /// </summary>
    static int[] Merge(UInt128[] keys, List<int> bounds)
    {
        var order = new int[keys.Length];
        for (var k = 0; k < order.Length; k++) order[k] = k;
        for (var k = 1; k < keys.Length; k++)
            if (keys[k] < keys[k - 1] && !bounds.Contains(k))
            {
                Array.Sort(order, (x, y) => keys[x].CompareTo(keys[y]));
                return order;
            }
        var buffer = new int[keys.Length];
        while (bounds.Count > 2)
        {
            var next = new List<int> { 0 };
            for (var r = 0; r + 1 < bounds.Count; r += 2)
            {
                int lo = bounds[r], mid = bounds[r + 1], hi = r + 2 < bounds.Count ? bounds[r + 2] : mid;
                int i = lo, j = mid, k = lo;
                while (i < mid && j < hi) buffer[k++] = keys[order[j]] < keys[order[i]] ? order[j++] : order[i++];
                while (i < mid) buffer[k++] = order[i++];
                while (j < hi) buffer[k++] = order[j++];
                next.Add(hi);
            }
            (order, buffer, bounds) = (buffer, order, next);
        }
        return order;
    }

    /// <summary>Interleaved bits with i lowest: the order of the octree's child index i | j·2 | k·4, level by level.</summary>
    static ulong Morton(int i, int j, int k) => Spread(i) | Spread(j) << 1 | Spread(k) << 2;

    static ulong Spread(int value)
    {
        var x = (ulong)(uint)value & 0x1FFFFF;
        x = (x | x << 32) & 0x1F00000000FFFF;
        x = (x | x << 16) & 0x1F0000FF0000FF;
        x = (x | x << 8) & 0x100F00F00F00F00F;
        x = (x | x << 4) & 0x10C30C30C30C30C3;
        x = (x | x << 2) & 0x1249249249249249;
        return x;
    }

    /// <summary>
    /// Where the traversal (<see cref="CellProc"/>, <see cref="FaceProc"/>, <see cref="EdgeProc"/>) reaches a minimal
    /// edge, as the digits of its recursion, 5 bits each from the top: sorting by it gives the order of Build. The
    /// path depends only on the edge's place and length: at each level the edge lies inside a child, in a face
    /// between children or on an edge between them, and the recursion ends where its span is the edge's length.
    /// Roots up to 2²⁴ cells fit.
    /// </summary>
    static UInt128 TraversalKey(int ai, int aj, int ak, int e, int length, int rootSize)
    {
        Span<int> a = [ai, aj, ak];
        Span<int> p = [0, 0, 0];
        Span<int> side = [0, 0, 0];
        var size = rootSize;
        UInt128 key = 0;
        var shift = 123;
        void Digit(int d, ref UInt128 key, ref int shift)
        {
            key |= (UInt128)(uint)d << shift;
            shift -= 5;
        }
        int e1 = (e + 1) % 3, e2 = (e + 2) % 3;

        // Cells: descend while the edge is inside one child.
        int face;
        while (true)
        {
            var half = size / 2;
            bool on1 = a[e1] == p[e1] + half, on2 = a[e2] == p[e2] + half;
            for (var x = 0; x < 3; x++)
            {
                side[x] = a[x] >= p[x] + half ? 1 : 0;
                p[x] += side[x] * half;
            }
            size = half;
            if (!on1 && !on2)
            {
                Digit(side[0] | side[1] << 1 | side[2] << 2, ref key, ref shift);
                continue;
            }
            if (on1 && on2)
            {
                Digit(20 + 2 * e + side[e], ref key, ref shift);
                face = -1;
                break;
            }
            face = on1 ? e1 : e2;
            Digit(8 + 4 * face + (side[(face + 1) % 3] | side[(face + 2) % 3] << 1), ref key, ref shift);
            break;
        }

        // Faces: descend while the edge is inside one quarter, until it lies on the face's middle line.
        if (face >= 0)
            while (true)
            {
                var half = size / 2;
                int b = (face + 1) % 3, c = (face + 2) % 3;
                var across = e == b ? c : b;
                int sb = a[b] >= p[b] + half ? 1 : 0, sc = a[c] >= p[c] + half ? 1 : 0;
                var onLine = a[across] == p[across] + half;
                (p[b], p[c], size) = (p[b] + sb * half, p[c] + sc * half, half);
                if (!onLine)
                {
                    Digit(sb | sc << 1, ref key, ref shift);
                    continue;
                }
                Digit(4 + 2 * (e == b ? 0 : 1) + (e == b ? sb : sc), ref key, ref shift);
                break;
            }

        // Edges: halve the span until it is the edge.
        while (size != length)
        {
            var half = size / 2;
            var t = a[e] >= p[e] + half ? 1 : 0;
            p[e] += t * half;
            size = half;
            Digit(t, ref key, ref shift);
        }
        return key;
    }

    /// <summary>
    /// A content hash of fields: node kinds, parameters and surface geometry, with surfaces numbered in order of first
    /// appearance (ids are compile order and change when a feature is inserted). Two lanes of SplitMix64: 128 bits.
    /// Source names are left out: a cached owner is renumbered by its place in the key, which also covers a rename.
    /// </summary>
    sealed class Fingerprint
    {
        static readonly ConditionalWeakTable<Field, object> Unknown = new();
        static long _unknown;

        readonly Dictionary<int, int> _local = new();
        readonly HashSet<int> _described = new();
        ulong _a = 0x9E3779B97F4A7C15, _b = 0xD1B54A32D192ED03;

        public List<int> Surfaces { get; } = new();

        public UInt128 Hash => new(_a, _b);

        public void Add(long v)
        {
            _a = Mix(_a ^ (ulong)v);
            _b = Mix(_b + (ulong)v * 0xFF51AFD7ED558CCD);
        }

        public void Add(double v) => Add(BitConverter.DoubleToInt64Bits(v));

        public void Add(bool v) => Add(v ? 1L : 0L);

        public void Add(Point3D p)
        {
            Add(p.X);
            Add(p.Y);
            Add(p.Z);
        }

        public void Add(Vector3D v)
        {
            Add(v.X);
            Add(v.Y);
            Add(v.Z);
        }

        static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }

        /// <summary>A surface by its local number; the first time also its geometry.</summary>
        void Add(Surface s)
        {
            Id(s.Id);
            if (!_described.Add(s.Id)) return;
            Add((int)s.Kind);
            Add(s.Origin);
            Add(s.Axis);
            Add(s.Radius);
            Add(s.Minor);
            Add(s.Angle);
        }

        /// <summary>A surface known by id only (a shell's inner surface: its geometry follows from the field).</summary>
        void Id(int id)
        {
            if (!_local.TryGetValue(id, out var k))
            {
                _local[id] = k = Surfaces.Count;
                Surfaces.Add(id);
            }
            Add(k);
        }

        void Add(Frame f)
        {
            Add(f.Origin);
            Add(f.X);
            Add(f.Y);
            Add(f.Z);
        }

        public void Add(Field? field)
        {
            switch (field)
            {
                case null:
                    Add(0);
                    break;
                case LeafField l:
                    Add(1);
                    Add(l.Complement);
                    Add(l.Surface);
                    break;
                case MaxField m:
                    Add(2);
                    Add(m.Children.Count);
                    foreach (var c in m.Children) Add(c);
                    break;
                case MinField m:
                    Add(3);
                    Add(m.Children.Count);
                    foreach (var c in m.Children) Add(c);
                    break;
                case NegateField n:
                    Add(4);
                    Add(n.Child);
                    break;
                case RoundedEdgeField r:
                    Add(5);
                    Add(r.A);
                    Add(r.B);
                    Add(r.Radius);
                    Add(r.Fillet);
                    break;
                case ShellField s:
                    Add(6);
                    Add(s.Thickness);
                    Add(s.Child);
                    Add(ReferenceEquals(s.Outer, s.Child));
                    if (!ReferenceEquals(s.Outer, s.Child)) Add(s.Outer);
                    Add(s.InnerOf.Count);
                    foreach (var (outer, inner) in s.InnerOf)
                    {
                        Id(outer);
                        Id(inner);
                    }
                    break;
                case BlendField b:
                    Add(7);
                    Add(b.A);
                    Add(b.B);
                    Add(b.K);
                    Add(b.Blend);
                    Add(b.Union);
                    break;
                case LoopEdgeField l:
                    Add(8);
                    Add(l.Cap);
                    Add(l.Side);
                    Add(l.Size);
                    Add(l.Round);
                    Add(l.FaceOf.Count);
                    foreach (var (side, face) in l.FaceOf)
                    {
                        Id(side);
                        Add(face);
                    }
                    break;
                case ProfileField p:
                    Add(9);
                    Add(p.Edges.Count);
                    for (var k = 0; k < p.Edges.Count; k++)
                    {
                        var e = p.Edges[k];
                        Add(e.U0);
                        Add(e.V0);
                        Add(e.U1);
                        Add(e.V1);
                        Add(e.Bulge);
                        Add(p.EdgeLeaves[k]);
                        Add(p.Corners[k]);
                    }
                    Add(p.Frame);
                    Add(p.Revolved);
                    Add(p.Grown);
                    break;
                case PlacedField p:
                    Add(10);
                    Add(p.Placement);
                    Add(p.Child);
                    break;
                case VertexSphereField v:
                    Add(13);
                    foreach (var e in v.Edges) Add(e);
                    Add(v.Sphere);
                    break;
                case LatticeField l:
                    // Every parameter it reads, and what a simplified copy still evaluates.
                    Add(12);
                    Add((int)l.Kind);
                    Add(l.Cell);
                    Add(l.Thickness);
                    Add(l.Sign);
                    Add(l.Complement);
                    Add(l.Grow);
                    Add(l.Active.Count);
                    foreach (var i in l.Active) Add(i);
                    Add(l.Surface);
                    break;
                default:
                    // A kind this hash does not know: unique per object, so it never matches another compile.
                    Add(11);
                    Add((long)Unknown.GetValue(field, _ => Interlocked.Increment(ref _unknown)));
                    break;
            }
        }
    }
}
