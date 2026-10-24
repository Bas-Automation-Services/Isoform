using System.Collections.Concurrent;
using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Meshing;

/// <summary>
/// A polygon of the surface mesh, labelled with the surface owning the grid-edge crossing it is dual to.
/// A triangle repeats its last corner: <see cref="D"/> equals <see cref="C"/>.
/// </summary>
public readonly record struct Quad(int A, int B, int C, int D, int Owner)
{
    public bool IsTriangle => C == D;
}

public readonly record struct Triangle(int A, int B, int C, int Owner);

/// <summary>Material or a gap between two surfaces that is thinner than the finest cells can resolve.</summary>
public readonly record struct ThinSpot(int OwnerA, int OwnerB, Point3D Location, double Thickness);

/// <summary>
/// A point of a cell without a vertex where the field has the other sign than at the cell's corners: |Value| is how
/// far the material (or gap) there reaches round it, Corner how far the cell's nearest corner is.
/// </summary>
public readonly record struct Pocket(Point3D Location, double Value, double Corner);

public sealed class Mesh(IReadOnlyList<Point3D> vertices, IReadOnlyList<Quad> quads, IReadOnlyList<ThinSpot>? thinSpots = null, IReadOnlyList<Pocket>? pockets = null)
{
    public IReadOnlyList<Point3D> Vertices { get; } = vertices;
    public IReadOnlyList<Quad> Quads { get; } = quads;
    public IReadOnlyList<ThinSpot> ThinSpots { get; } = thinSpots ?? [];
    public IReadOnlyList<Pocket> Pockets { get; } = pockets ?? [];

    /// <summary>Each quad split along its shorter diagonal.</summary>
    public IEnumerable<Triangle> Triangles()
    {
        foreach (var q in Quads)
        {
            if (q.IsTriangle)
                yield return new(q.A, q.B, q.C, q.Owner);
            else if ((Vertices[q.A] - Vertices[q.C]).LengthSquared <= (Vertices[q.B] - Vertices[q.D]).LengthSquared)
            {
                yield return new(q.A, q.B, q.C, q.Owner);
                yield return new(q.A, q.C, q.D, q.Owner);
            }
            else
            {
                yield return new(q.A, q.B, q.D, q.Owner);
                yield return new(q.B, q.C, q.D, q.Owner);
            }
        }
    }

    /// <summary>Every undirected edge used by exactly two triangles, in opposite directions.</summary>
    public bool IsWatertight()
    {
        var directed = new Dictionary<(int, int), int>();
        foreach (var t in Triangles())
            foreach (var e in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                directed[e] = directed.GetValueOrDefault(e) + 1;
        return directed.All(kv => kv.Value == 1 && directed.GetValueOrDefault((kv.Key.Item2, kv.Key.Item1)) == 1);
    }
}

/// <summary>
/// Adaptive dual contouring on an octree (Ju et al. 2002). A cell is only refined where it has to be:
/// <list type="bullet">
/// <item>interval bounds that prove a cell empty or solid make it a leaf at any size;</item>
/// <item>a cell holding one plane stays coarse at any size, one curved surface while its chords stay within the
/// chord tolerance;</item>
/// <item>everything else (edges, corners, several surfaces) is refined to the finest cell size.</item>
/// </list>
/// Polygons are dual to the octree's minimal edges, so cells of different size never leave cracks. Crossings are
/// roots of the exact field, normals its exact gradients, and each cell vertex minimises the quadric error of its
/// crossings' tangent planes, so sharp edges and corners are reproduced. A finest cell the surface passes through in
/// several pieces gets a vertex per piece, so the mesh stays manifold. Output is deterministic.
/// </summary>
public static partial class DualContouring
{
    // Shifts the grid off round numbers so grid points never land exactly on axis-aligned faces.
    const double GridShift = 0.31830988618379067;

    // Certify only with a margin, so floating-point noise in a bound can never hide a real sign change.
    const double CertifyMargin = 1e-9;

    // Octree levels below the root that are built and traversed in parallel. Flat parts fill only a few of the
    // top-level nodes, so this goes deeper than the core count alone would suggest.
    const int ParallelDepth = 4;

    internal readonly record struct Crossing(Point3D Point, Vector3D Normal, int Owner);

    /// <summary>
    /// A sign-changing minimal edge, the four leaves around it in counter-clockwise order, and the smallest. The edge
    /// starts at grid point (I, J, K) and runs along <see cref="Axis"/> for the smallest leaf's size.
    /// </summary>
    readonly record struct Hit(Point3D P0, Point3D P1, bool StartInside, Node N0, Node N1, Node N2, Node N3, Node Smallest, int I, int J, int K, int Axis);

    internal sealed class Node(int i, int j, int k, int size)
    {
        public readonly int I = i, J = j, K = k, Size = size;
        public Node[]? Children;

        /// <summary>Set on leaves that hold surface: the field simplified for the node's closed box.</summary>
        public Field? Field;

        /// <summary>A finest leaf holding more than one surface.</summary>
        public bool Multi;

        /// <summary>The finest cell where a leaf is, in grid units: its own size if it is a finest leaf.</summary>
        public int Unit = 1;

        /// <summary>A finest leaf beside a finer zone: its neighbours may be smaller, so it keeps one vertex.</summary>
        public bool Border;

        /// <summary>A region of a <see cref="MeshPlan"/>, not split yet: the field simplified for the node's box.</summary>
        public Field? Pending;

        public int Id = -1;
        public bool IsLeaf => Children is null;
        public bool IsEmpty => Children is null && Field is null;
    }

    /// <param name="UnitDepth">Depth at which a plan stops splitting and leaves the node pending; -1 never stops.</param>
    /// <param name="Region">A region job builds only the nodes touching this node's closed box.</param>
    /// <param name="Serial">No nested parallelism: region jobs run in parallel with each other instead.</param>
    /// <param name="Coarse">The finest cell outside every zone, in grid units: 1 unless the grid was refined.</param>
    /// <param name="Zones">Boxes of grid cells refined to a finer cell.</param>
    internal sealed record Context(Grid Grid, bool Prune, bool Adaptive, double ChordTolerance, int UnitDepth = -1, Node? Region = null, bool Serial = false,
        int Coarse = 1, Zone[]? Zones = null)
    {
        /// <summary>The finest cell a node may be split to: the smallest of the zones it overlaps.</summary>
        public int Finest(Node n)
        {
            var unit = Coarse;
            if (Zones is { } zones)
                foreach (var z in zones)
                    if (z.Unit < unit && z.Overlaps(n.I, n.J, n.K, n.Size, 0)) unit = z.Unit;
            return unit;
        }

        /// <summary>A zone finer than the node lies within one node size of it.</summary>
        public bool FinerNear(Node n)
        {
            if (Zones is { } zones)
                foreach (var z in zones)
                    if (z.Unit < n.Size && z.Overlaps(n.I, n.J, n.K, n.Size, n.Size)) return true;
            return false;
        }
    }

    /// <summary>Grid cells [I0, I1) × [J0, J1) × [K0, K1) whose finest cell is <see cref="Unit"/> grid units.</summary>
    internal readonly record struct Zone(int I0, int J0, int K0, int I1, int J1, int K1, int Unit)
    {
        public bool Overlaps(int i, int j, int k, int size, int grow) =>
            i - grow < I1 && i + size + grow > I0 && j - grow < J1 && j + size + grow > J0 && k - grow < K1 && k + size + grow > K0;
    }

    /// <param name="chordTolerance">Largest distance a polygon may stray from a curved surface away from edges.</param>
    /// <param name="adaptive">False refines every surface cell to <paramref name="cellSize"/>.</param>
    /// <param name="prune">
    /// Skip cells whose interval bounds prove they hold no surface, and simplify the field per cell. With
    /// <paramref name="adaptive"/> false the mesh is identical either way; false exists to test exactly that.
    /// </param>
    /// <param name="snapGrid">Put the lattice on a fixed world grid instead of the part's corner (see <see cref="Plan"/>).</param>
    public static Mesh Build(Field field, Point3D min, Point3D max, double cellSize, double chordTolerance,
        Action<string, TimeSpan>? trace = null, bool adaptive = true, bool prune = true, bool snapGrid = false)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Stage(FormattableString name)
        {
            trace?.Invoke(FormattableString.Invariant(name), clock.Elapsed);
            clock.Restart();
        }

        var grid = Grid.Around(min, max, cellSize, snapGrid);
        var ctx = new Context(grid, prune, adaptive && prune, chordTolerance);

        var root = new Node(0, 0, 0, grid.RootSize);
        Subdivide(root, field, ctx, 0);
        var leaves = new List<Node>();
        Number(root, leaves);
        Stage($"octree ({leaves.Count:N0} surface cells)");

        var hits = new List<Hit>();
        CellProc(root, hits, ctx, 0);
        Stage($"sign changes ({hits.Count:N0} edges)");

        var crossings = new Crossing[hits.Count];
        Parallel.For(0, hits.Count, e =>
        {
            var hit = hits[e];
            var f = hit.Smallest.Field!;
            var c = FindRoot(f, hit.P0, hit.P1);
            var sample = f.Sample(c);
            crossings[e] = new(c, sample.Gradient.Normalized(), sample.Owner);
        });
        Stage($"crossings (root finding)");

        // Sorting (cell, edge) groups the crossings per cell, puts cells in ascending order and keeps each cell's
        // crossings in edge order. A leaf beside smaller neighbours can fill two quadrants; it takes the crossing once.
        var pairs = new List<long>(hits.Count * 4);
        for (var e = 0; e < hits.Count; e++)
        {
            var (a, b, c, d) = (hits[e].N0, hits[e].N1, hits[e].N2, hits[e].N3);
            pairs.Add(((long)a.Id << 32) | (uint)e);
            if (b != a) pairs.Add(((long)b.Id << 32) | (uint)e);
            if (c != a && c != b) pairs.Add(((long)c.Id << 32) | (uint)e);
            if (d != a && d != b && d != c) pairs.Add(((long)d.Id << 32) | (uint)e);
        }
        pairs.Sort();
        var cellStart = new List<int>();
        for (var k = 0; k < pairs.Count; k++)
            if (k == 0 || pairs[k] >> 32 != pairs[k - 1] >> 32) cellStart.Add(k);
        cellStart.Add(pairs.Count);
        var cellCount = cellStart.Count - 1;
        var cellOfLeaf = new int[leaves.Count];
        for (var v = 0; v < cellCount; v++) cellOfLeaf[(int)(pairs[cellStart[v]] >> 32)] = v;

        // A cell cut into several surface pieces gets a vertex per piece: its first vertex and the piece of each edge.
        // Only a leaf holding several surfaces can be (one analytic patch passes a finest cell once).
        var pieces = new sbyte[]?[cellCount];
        Parallel.For(0, cellCount, v =>
        {
            var node = leaves[(int)(pairs[cellStart[v]] >> 32)];
            if ((prune ? node.Multi : node.Size == 1) && cellStart[v + 1] - cellStart[v] >= 6) pieces[v] = Pieces(node, grid);
        });
        var firstVertex = new int[cellCount + 1];
        for (var v = 0; v < cellCount; v++) firstVertex[v + 1] = firstVertex[v] + PieceCount(pieces[v]);

        var spots = new ConcurrentBag<ThinSpot>();
        var regions = new ConcurrentDictionary<(int, int, int, int), Field>();
        var positions = new Point3D[firstVertex[cellCount]];
        Parallel.For(0, cellCount, v =>
        {
            var n = cellStart[v + 1] - cellStart[v];
            Span<Crossing> local = n <= 48 ? stackalloc Crossing[48] : new Crossing[n];
            var node = leaves[(int)(pairs[cellStart[v]] >> 32)];
            for (var piece = 0; piece < PieceCount(pieces[v]); piece++)
            {
                var m = 0;
                for (var k = 0; k < n; k++)
                {
                    var e = (int)(uint)pairs[cellStart[v] + k];
                    if (pieces[v] is not { } t || t[LocalEdge(node, hits[e])] == piece) local[m++] = crossings[e];
                }
                // A vertex on one surface is projected onto that surface.
                var near = !prune ? field : SingleOwner(local[..m]) ? node.Field! : Near(field, grid, node,
                    regions.GetOrAdd(grid.UnitOf(node), u => field.Simplify(grid.Box(u, 1))), out _);
                positions[firstVertex[v] + piece] = CellVertex(near, field, grid, node, local[..m], prune ? spots : null, out _);
            }
        });
        Stage($"cell vertices ({cellCount:N0} cells)");

        int Vertex(Node node, in Hit hit)
        {
            var v = cellOfLeaf[node.Id];
            return firstVertex[v] + (pieces[v] is { } t ? t[LocalEdge(node, hit)] : 0);
        }
        var quads = new List<Quad>(hits.Count);
        for (var e = 0; e < hits.Count; e++)
        {
            var hit = hits[e];
            quads.Add(Polygon(Vertex(hit.N0, hit), Vertex(hit.N1, hit), Vertex(hit.N2, hit), Vertex(hit.N3, hit), hit.StartInside, crossings[e].Owner));
        }
        Stage($"polygons ({quads.Count:N0})");

        if (prune) Parallel.ForEach(leaves.Where(l => l.Multi), node => FindThin(node, grid, spots));
        var thin = Sorted(spots);
        Stage($"thin spots ({thin.Count:N0})");

        // Leaves round no crossing have no vertex: the field may still take the other sign inside them.
        var vertexless = new bool[leaves.Count];
        if (prune) Array.Fill(vertexless, true);
        for (var v = 0; v < cellCount; v++) vertexless[(int)(pairs[cellStart[v]] >> 32)] = false;
        var found = new ConcurrentBag<Pocket>();
        Parallel.For(0, leaves.Count, l =>
        {
            if (vertexless[l] && FindPocket(leaves[l], grid) is { } p) found.Add(p);
        });
        var pockets = Sorted(found);
        Stage($"pockets ({pockets.Count:N0} in {vertexless.Count(x => x):N0} cells without a vertex)");

        return new Mesh(positions, quads, thin, pockets);
    }

    static List<ThinSpot> Sorted(IEnumerable<ThinSpot> spots) =>
        spots.OrderBy(s => s.OwnerA).ThenBy(s => s.OwnerB).ThenBy(s => s.Location.X).ThenBy(s => s.Location.Y).ThenBy(s => s.Location.Z).ToList();

    /// <summary>The polygon round an edge from the vertices of its four leaves, in counter-clockwise order.</summary>
    static Quad Polygon(int v0, int v1, int v2, int v3, bool startInside, int owner)
    {
        Span<int> around = [v0, v1, v2, v3];
        Span<int> ring = stackalloc int[4];
        // Drop a repeated neighbour (duplicates are adjacent around the edge), keeping the cyclic order.
        var count = 0;
        for (var q = 0; q < 4; q++)
            if (around[q] != around[(q + 3) % 4]) ring[count++] = around[q];
        // Wind so the polygon faces outward: away from the inside end of the edge.
        if (count == 4)
            return startInside ? new Quad(ring[0], ring[1], ring[2], ring[3], owner) : new Quad(ring[3], ring[2], ring[1], ring[0], owner);
        return startInside ? new Quad(ring[0], ring[1], ring[2], ring[2], owner) : new Quad(ring[2], ring[1], ring[0], ring[0], owner);
    }

    /// <summary>
    /// Where several surfaces meet the vertex may sit up to half a cell outside the node, so it gets a field exact one
    /// node size around it. That field is simplified from the field of the node's region grown by one cell (which
    /// region keys hash), so a region's vertices depend on nothing outside it. Only a coarse node at the side of its
    /// region reaches further and takes the whole field; <paramref name="local"/> is then false. A vertex that strays
    /// further (<see cref="Reach"/>) reads the whole field too.
    /// </summary>
    static Field Near(Field field, Grid grid, Node node, Field region, out bool local)
    {
        var u = grid.UnitOf(node);
        var s = node.Size;
        local = node.I - s >= u.I - 1 && node.J - s >= u.J - 1 && node.K - s >= u.K - 1
            && node.I + 2 * s <= u.I + u.Size + 1 && node.J + 2 * s <= u.J + u.Size + 1 && node.K + 2 * s <= u.K + u.Size + 1;
        return (local ? region : field).Simplify(grid.Box(node, s));
    }

    // Cells a vertex may stray from its cell to sit on a real edge of the solid: the tip of a wedge too thin to hold
    // grid points. Beyond that the wedge is reported (FindThin), so the two agree on four cells.
    const int Reach = 4;

    // A vertex this close to the field's zero is on the solid (they normally land within 1e-15).
    const double OnSolid = 1e-10;

    static void Subdivide(Node node, Field field, Context ctx, int depth)
    {
        var g = ctx.Grid;
        if (node.I >= g.Nx || node.J >= g.Ny || node.K >= g.Nz) return; // beyond the part's margin: empty
        if (ctx.Region is { } region && !Touches(node, region)) return; // a region job builds its neighbours only where they touch it
        var box = g.Box(node);
        if (ctx.Prune)
        {
            // Simplifying never changes the bounds, so certify first: empty cells then cost one bounds pass.
            // Survivors are simplified, and their children are bounded on the smaller tree.
            var bounds = field.Bounds(box);
            if (bounds.Lo > CertifyMargin || bounds.Hi < -CertifyMargin) return;
            field = field.Simplify(box);
        }
        var patch = ctx.Prune ? field.SinglePatch(box) : null;
        var finest = ctx.Finest(node);
        if (node.Size <= finest || ctx.Adaptive && patch is { } r && node.Size * g.H <= MaxCurvedCell(r, ctx.ChordTolerance))
        {
            node.Field = field;
            node.Unit = Math.Min(finest, node.Size);
            node.Multi = ctx.Prune && node.Size <= finest && patch is null;
            node.Border = node.Multi && ctx.FinerNear(node);
            return;
        }
        if (depth == ctx.UnitDepth)
        {
            node.Pending = field;
            return;
        }
        Split(node, field, ctx, depth);
    }

    static void Split(Node node, Field field, Context ctx, int depth)
    {
        var half = node.Size / 2;
        var children = new Node[8];
        for (var c = 0; c < 8; c++)
            children[c] = new Node(node.I + (c & 1) * half, node.J + (c >> 1 & 1) * half, node.K + (c >> 2) * half, half);
        node.Children = children;
        if (depth < ParallelDepth && !ctx.Serial) Parallel.For(0, 8, c => Subdivide(children[c], field, ctx, depth + 1));
        else foreach (var child in children) Subdivide(child, field, ctx, depth + 1);
    }

    /// <summary>The closed boxes of two nodes meet, if only in a corner.</summary>
    static bool Touches(Node a, Node b) =>
        a.I <= b.I + b.Size && b.I <= a.I + a.Size && a.J <= b.J + b.Size && b.J <= a.J + a.Size && a.K <= b.K + b.Size && b.K <= a.K + a.Size;

    /// <summary>The node lies in the region (whose leaves own it).</summary>
    static bool Within(Node n, Node region) =>
        n.I >= region.I && n.I < region.I + region.Size && n.J >= region.J && n.J < region.J + region.Size && n.K >= region.K && n.K < region.K + region.Size;

    /// <summary>
    /// Largest cell on a surface of curvature radius r. A chord of length c strays c²/8r from the arc. The longest
    /// chords measured are about 1.15 cells, so s = √(4rε) keeps them within about 0.65ε.
    /// </summary>
    static double MaxCurvedCell(double radius, double chordTolerance) =>
        double.IsPositiveInfinity(radius) ? double.PositiveInfinity : Math.Sqrt(4 * radius * chordTolerance);

    static void Number(Node node, List<Node> leaves)
    {
        if (node.Children is { } ch)
            foreach (var c in ch) Number(c, leaves);
        else if (node.Field is not null)
        {
            node.Id = leaves.Count;
            leaves.Add(node);
        }
    }

    // ---- Octree traversal (Ju et al.): every minimal edge is visited once, with the four leaves around it. ----

    /// <summary>1 if quadrant q (counter-clockwise round an edge along e) lies on the + side of <paramref name="axis"/>.</summary>
    static int Side(int q, int axis, int e) => axis == (e + 1) % 3 ? (q == 1 || q == 2 ? 1 : 0) : (q >= 2 ? 1 : 0);

    static Node Child(Node n, int index) => n.Children is { } ch ? ch[index] : n;

    static void CellProc(Node n, List<Hit> output, Context ctx, int depth)
    {
        if (n.Children is not { } ch) return;
        if (ctx.Region is { } r && !Touches(n, r)) return; // a region job only looks for edges at its region
        if (depth < ParallelDepth && !ctx.Serial)
        {
            var parts = new List<Hit>[26];
            Parallel.For(0, 26, t => CellPart(ch, t, parts[t] = [], ctx, depth));
            foreach (var p in parts) output.AddRange(p);
        }
        else
            for (var t = 0; t < 26; t++) CellPart(ch, t, output, ctx, depth);
    }

    /// <summary>Part t of a cell: its 8 children, then the 12 faces between them, then the 6 edges inside it.</summary>
    static void CellPart(Node[] ch, int t, List<Hit> output, Context ctx, int depth)
    {
        if (t < 8)
        {
            CellProc(ch[t], output, ctx, depth + 1);
            return;
        }
        if (t < 20)
        {
            var axis = (t - 8) / 4;
            var m = (t - 8) % 4;
            var low = ((m & 1) << (axis + 1) % 3) | ((m >> 1) << (axis + 2) % 3);
            FaceProc(ch[low], ch[low | 1 << axis], axis, output, ctx);
            return;
        }
        var e = (t - 20) / 2;
        var half = (t - 20) % 2;
        int e1 = (e + 1) % 3, e2 = (e + 2) % 3;
        Node At(int q) => ch[(half << e) | (Side(q, e1, e) << e1) | (Side(q, e2, e) << e2)];
        EdgeProc(At(0), At(1), At(2), At(3), e, output, ctx);
    }

    /// <summary>The face between n0 (low side of <paramref name="a"/>) and n1.</summary>
    static void FaceProc(Node n0, Node n1, int a, List<Hit> output, Context ctx)
    {
        if (n0.IsEmpty || n1.IsEmpty || n0.IsLeaf && n1.IsLeaf) return;
        if (ctx.Region is { } r && (!Touches(n0, r) || !Touches(n1, r))) return;
        int b = (a + 1) % 3, c = (a + 2) % 3;
        for (var m = 0; m < 4; m++)
        {
            var rest = ((m & 1) << b) | ((m >> 1) << c);
            FaceProc(Child(n0, rest | 1 << a), Child(n1, rest), a, output, ctx);
        }
        // The four edges inside the face, two along each of its axes.
        for (var pass = 0; pass < 2; pass++)
        {
            var (e, f) = pass == 0 ? (b, c) : (c, b);
            for (var t = 0; t < 2; t++)
            {
                Node At(int q)
                {
                    var high = Side(q, a, e) == 1;
                    return Child(high ? n1 : n0, (t << e) | ((high ? 0 : 1) << a) | (Side(q, f, e) << f));
                }
                EdgeProc(At(0), At(1), At(2), At(3), e, output, ctx);
            }
        }
    }

    static void EdgeProc(Node n0, Node n1, Node n2, Node n3, int e, List<Hit> output, Context ctx)
    {
        // An edge in the closed box of a certified-empty cell has no sign change.
        if (n0.IsEmpty || n1.IsEmpty || n2.IsEmpty || n3.IsEmpty) return;
        if (ctx.Region is { } r && (!Touches(n0, r) || !Touches(n1, r) || !Touches(n2, r) || !Touches(n3, r))) return;
        if (n0.IsLeaf && n1.IsLeaf && n2.IsLeaf && n3.IsLeaf)
        {
            ProcessEdge(n0, n1, n2, n3, e, output, ctx);
            return;
        }
        int e1 = (e + 1) % 3, e2 = (e + 2) % 3;
        for (var t = 0; t < 2; t++)
        {
            // The child touching the edge: on the − side of an axis it is the child at that axis' high end.
            Node Toward(Node n, int q) => Child(n, (t << e) | ((1 - Side(q, e1, e)) << e1) | ((1 - Side(q, e2, e)) << e2));
            EdgeProc(Toward(n0, 0), Toward(n1, 1), Toward(n2, 2), Toward(n3, 3), e, output, ctx);
        }
    }

    static void ProcessEdge(Node n0, Node n1, Node n2, Node n3, int e, List<Hit> output, Context ctx)
    {
        var (s, qs) = (n0, 0);
        if (n1.Size < s.Size) (s, qs) = (n1, 1);
        if (n2.Size < s.Size) (s, qs) = (n2, 2);
        if (n3.Size < s.Size) (s, qs) = (n3, 3);

        // The edge is an edge of the smallest leaf, on the sides facing the other three.
        int e1 = (e + 1) % 3, e2 = (e + 2) % 3;
        Span<int> p = [s.I, s.J, s.K];
        if (Side(qs, e1, e) == 0) p[e1] += s.Size;
        if (Side(qs, e2, e) == 0) p[e2] += s.Size;
        var (i, j, k) = (p[0], p[1], p[2]);
        var a = ctx.Grid.Position(i, j, k);
        p[e] += s.Size;
        var b = ctx.Grid.Position(p[0], p[1], p[2]);

        // The smallest leaf's field is exact on its closed box, so every leaf agrees on these signs.
        var inside = s.Field!.Value(a) < 0;
        if (inside != s.Field.Value(b) < 0) output.Add(new Hit(a, b, inside, n0, n1, n2, n3, s, i, j, k, e));
    }

    // ---- Vertices ----

    /// <summary>
    /// Root of the field on a sign-changing segment: Illinois (modified regula falsi), which keeps the bracket of
    /// bisection but converges superlinearly on the piecewise-smooth fields we have.
    /// </summary>
    static Point3D FindRoot(Field field, Point3D a, Point3D b)
    {
        var dir = b - a;
        double t0 = 0, t1 = 1;
        var f0 = field.Value(a);
        var f1 = field.Value(b);
        var side = 0;
        for (var it = 0; it < 80; it++)
        {
            var t = (t0 * f1 - t1 * f0) / (f1 - f0);
            if (!(t > t0 && t < t1)) t = (t0 + t1) / 2;
            var ft = field.Value(a + dir * t);
            if (ft == 0 || t1 - t0 < 1e-16 || Math.Abs(ft) < 1e-14) return a + dir * t;
            if (ft < 0 == f0 < 0)
            {
                (t0, f0) = (t, ft);
                if (side == -1) f1 /= 2;
                side = -1;
            }
            else
            {
                (t1, f1) = (t, ft);
                if (side == 1) f0 /= 2;
                side = 1;
            }
        }
        return a + dir * ((t0 + t1) / 2);
    }

    /// <summary>The edge of a hit in a finest leaf: axis·4, plus its offsets along the next two axes.</summary>
    static int LocalEdge(Node leaf, in Hit hit)
    {
        Span<int> d = [(hit.I - leaf.I) / leaf.Size, (hit.J - leaf.J) / leaf.Size, (hit.K - leaf.K) / leaf.Size];
        return 4 * hit.Axis + d[(hit.Axis + 1) % 3] + 2 * d[(hit.Axis + 2) % 3];
    }

    static int PieceCount(sbyte[]? pieces)
    {
        if (pieces is null) return 1;
        var max = 0;
        foreach (var p in pieces) max = Math.Max(max, p);
        return max + 1;
    }

    /// <summary>
    /// The pieces of surface in a finest leaf, as marching cubes cuts it: its sign-changing edges joined across each
    /// face, a face with four crossings split as the field in its middle says. Two sheets through one cell (a wedge
    /// thinner than the cell) are then two pieces with a vertex each, so their polygons do not share edges (manifold
    /// dual contouring, Schaefer et al. 2007). The piece of each local edge, -1 where it has no crossing; null for one
    /// piece, which is always so below six crossings. Only the leaf's own field is read, so every job agrees.
    /// </summary>
    static sbyte[]? Pieces(Node leaf, Grid grid)
    {
        if (leaf.Border) return null;
        var f = leaf.Field!;
        var size = leaf.Size;
        Point3D Corner(int c) => grid.Position(leaf.I + (c & 1) * size, leaf.J + (c >> 1 & 1) * size, leaf.K + (c >> 2) * size);
        var inside = 0; // bit c: corner c (x | y·2 | z·4) is inside
        for (var c = 0; c < 8; c++)
            if (f.Value(Corner(c)) < 0) inside |= 1 << c;
        bool Changes(int axis, int u, int v)
        {
            var c = u << (axis + 1) % 3 | v << (axis + 2) % 3;
            return (inside >> c & 1) != (inside >> (c | 1 << axis) & 1);
        }

        Span<int> parent = stackalloc int[12];
        var changes = 0;
        for (var e = 0; e < 12; e++)
        {
            parent[e] = e;
            if (Changes(e / 4, e & 1, e >> 1 & 1)) changes++;
        }
        if (changes < 6) return null;
        int Find(Span<int> parent, int e)
        {
            while (parent[e] != e) e = parent[e] = parent[parent[e]];
            return e;
        }
        void Union(Span<int> parent, int x, int y) => parent[Find(parent, x)] = Find(parent, y);

        Span<int> crossing = stackalloc int[4];
        for (var a = 0; a < 3; a++)
        for (var s = 0; s < 2; s++)
        {
            // Face across axis a at side s. Its edge along b at c-offset t is 4b + t + 2s, along c at b-offset t is
            // 4c + s + 2t; round its corner (x, y) run the edges along b at y and along c at x.
            int b = (a + 1) % 3, c = (a + 2) % 3;
            var n = 0;
            for (var t = 0; t < 2; t++)
            {
                if (Changes(b, t, s)) crossing[n++] = 4 * b + t + 2 * s;
                if (Changes(c, s, t)) crossing[n++] = 4 * c + s + 2 * t;
            }
            if (n == 2) Union(parent, crossing[0], crossing[1]);
            if (n != 4) continue;
            // Corners (0, 0) and (1, 1) share a sign; if the middle of the face has it too they connect, and the curves
            // run round the other two corners. The middle is the centroid of the four crossings (the roots every job
            // finds on these edges): between two straight curves it always lies in the band between them. On a
            // contact of two solids the field there is zero, and the solids are joined.
            var middle = new Vector3D(0, 0, 0);
            foreach (var e in crossing)
            {
                var (axis, u, v) = (e / 4, e & 1, e >> 1 & 1);
                var start = u << (axis + 1) % 3 | v << (axis + 2) % 3;
                middle += FindRoot(f, Corner(start), Corner(start | 1 << axis)).AsVector() / 4;
            }
            var joined = f.Value(new Point3D(middle.X, middle.Y, middle.Z)) <= 0 == ((inside >> (s << a) & 1) == 1);
            foreach (var (x, y) in joined ? [(1, 0), (0, 1)] : new[] { (0, 0), (1, 1) })
                Union(parent, 4 * b + y + 2 * s, 4 * c + s + 2 * x);
        }

        var pieces = new sbyte[12];
        Span<int> label = stackalloc int[12];
        label.Fill(-1);
        var count = 0;
        for (var e = 0; e < 12; e++)
        {
            pieces[e] = -1;
            if (!Changes(e / 4, e & 1, e >> 1 & 1)) continue;
            var root = Find(parent, e);
            if (label[root] < 0) label[root] = count++;
            pieces[e] = (sbyte)label[root];
        }
        return count == 1 ? null : pieces;
    }

    static bool SingleOwner(ReadOnlySpan<Crossing> crossings)
    {
        foreach (var c in crossings)
            if (c.Owner != crossings[0].Owner) return false;
        return true;
    }

    /// <param name="field">Exact on the node grown by its size.</param>
    /// <param name="whole">The whole field, for a vertex beyond that; <paramref name="wide"/> tells it was read.</param>
    /// <param name="spots">Where a cell one vertex cannot hold is reported.</param>
    static Point3D CellVertex(Field field, Field whole, Grid grid, Node node, ReadOnlySpan<Crossing> crossings, ConcurrentBag<ThinSpot>? spots, out bool wide)
    {
        wide = false;
        var mass = new Vector3D(0, 0, 0);
        foreach (var c in crossings) mass += c.Point.AsVector();
        var m = mass / crossings.Length;
        var mp = new Point3D(m.X, m.Y, m.Z);

        // Distinct owners in order of first appearance.
        Span<int> owners = stackalloc int[crossings.Length + 1];
        var ownerCount = 0;
        foreach (var c in crossings)
            if (!owners[..ownerCount].Contains(c.Owner)) owners[ownerCount++] = c.Owner;
        if (ownerCount == 1) return Project(field, mp);

        // Minimise Σ (nᵢ·(x − pᵢ))² around the mass point with a truncated pseudo-inverse.
        var ata = new double[3, 3];
        var atb = new double[3];
        foreach (var c in crossings)
        {
            double[] n = [c.Normal.X, c.Normal.Y, c.Normal.Z];
            var d = c.Normal.Dot(c.Point - mp);
            for (var r = 0; r < 3; r++)
            {
                atb[r] += n[r] * d;
                for (var s = 0; s < 3; s++) ata[r, s] += n[r] * n[s];
            }
        }
        var x = SymmetricSolver.PseudoSolve(ata, atb, relativeCutoff: 0.05);
        var edge = Refine(field, new Point3D(mp.X + x[0], mp.Y + x[1], mp.Z + x[2]), owners[..ownerCount]);
        // Tangent (G1) junctions leave the along-face position undetermined; land on the solid itself.
        var v = Project(field, edge);
        var box = grid.Box(node);
        if (Inside(box, v, node.Size * grid.H / 2))
            return Math.Abs(field.Value(v)) <= OnSolid ? v : OntoOwner(field, v, owners[..ownerCount], box, node.Size * grid.H / 2) ?? v;
        // The tip of a wedge too thin to hold grid points lies beyond the cells it cuts; where the vertex is on every
        // owner and on the solid it is that edge, and goes there. That far out only the whole field is exact.
        wide = true;
        var far = whole.Simplify(grid.Box(node, Math.Max(node.Size, Reach * node.Unit)));
        v = Project(far, edge);
        var reach = Reach * node.Unit * grid.H;
        if (Inside(box, v, reach) && OnEvery(far, v, owners[..ownerCount])) return v;
        // Or the edge ends nearby on a surface no crossing saw, as where a wedge's tip runs into a side face: the corner.
        var cut = far.Sample(edge).Owner;
        if (!owners[..ownerCount].Contains(cut))
        {
            owners[ownerCount++] = cut;
            v = Project(far, Refine(far, edge, owners[..ownerCount]));
            if (Inside(box, v, reach) && OnEvery(far, v, owners[..ownerCount])) return v;
        }
        // One vertex cannot hold this cell's surfaces. Where two of them face each other, the material or gap between
        // them is thinner than the cell (a wedge cut short by a side face), and is reported.
        if (spots is not null)
            foreach (var a in crossings)
            foreach (var b in crossings)
                if (a.Owner < b.Owner && a.Normal.Dot(b.Normal) < -0.5)
                {
                    var thickness = Math.Abs(field.SampleSurface(a.Owner, mp)!.Value.Value) + Math.Abs(field.SampleSurface(b.Owner, mp)!.Value.Value);
                    spots.Add(new ThinSpot(a.Owner, b.Owner, mp, thickness));
                    return mp;
                }
        // Or the cell holds a real edge of two of its surfaces, a crossing's and maybe one no crossing saw, as beside the
        // cusp where a piece of face tapers between two tangent surfaces (a fillet run's end at a reflex corner) and
        // their corner with it cannot be reached: that edge.
        var all = new List<int>(owners[..ownerCount].ToArray());
        Surfaces(field, all);
        for (var i = 0; i < ownerCount; i++)
        for (var j = i + 1; j < all.Count; j++)
            if (Edge(field, mp, all[i], all[j]) is { } e && Inside(box, e, 0)) return e;
        return mp;
    }

    /// <summary>
    /// A point of the solid's boundary on both surfaces, by Gauss-Newton from <paramref name="p"/> (as in
    /// <see cref="Refine"/>); null where it does not get there or a surface has no value on the way.
    /// </summary>
    static Point3D? Edge(Field field, Point3D p, int a, int b)
    {
        for (var it = 0; it < 8; it++)
        {
            if (field.SampleSurface(a, p) is not { } sa || field.SampleSurface(b, p) is not { } sb) return null;
            var jtj = new double[3, 3];
            var jtr = new double[3];
            foreach (var s in (FieldSample[])[sa, sb])
            {
                double[] g = [s.Gradient.X, s.Gradient.Y, s.Gradient.Z];
                for (var r = 0; r < 3; r++)
                {
                    jtr[r] -= g[r] * s.Value;
                    for (var c = 0; c < 3; c++) jtj[r, c] += g[r] * g[c];
                }
            }
            var dx = SymmetricSolver.PseudoSolve(jtj, jtr, relativeCutoff: 1e-6);
            p = new Point3D(p.X + dx[0], p.Y + dx[1], p.Z + dx[2]);
        }
        p = Project(field, p);
        return Math.Abs(field.Value(p)) <= 1e-9 && field.SampleSurface(a, p) is { } x && Math.Abs(x.Value) <= 1e-9
            && field.SampleSurface(b, p) is { } y && Math.Abs(y.Value) <= 1e-9 ? p : null;
    }

    /// <summary>
    /// Where four faces meet tangentially the owners have no common point, and their least-squares point can sit in a
    /// sliver between two of them, where the field has a positive minimum and Newton rocks between the two. One of the
    /// owners' own surfaces is the solid's there: the nearest point of the cell on the solid that one reaches, if any.
    /// </summary>
    static Point3D? OntoOwner(Field field, Point3D p, ReadOnlySpan<int> owners, Box3D box, double margin)
    {
        Point3D? best = null;
        foreach (var owner in owners)
        {
            var q = OntoSurface(field, owner, p);
            if (Math.Abs(field.Value(q)) <= OnSolid && Inside(box, q, margin) && (best is not { } b || (q - p).LengthSquared < (b - p).LengthSquared)) best = q;
        }
        return best;
    }

    static bool OnEvery(Field field, Point3D p, ReadOnlySpan<int> owners)
    {
        if (Math.Abs(field.Value(p)) > 1e-9) return false;
        foreach (var owner in owners)
            if (Math.Abs(field.SampleSurface(owner, p)!.Value.Value) > 1e-9) return false;
        return true;
    }

    // The QEF uses tangent planes; Gauss-Newton on the owner surfaces themselves moves the vertex onto the
    // real (possibly curved) edge or corner.
    static Point3D Refine(Field field, Point3D p, ReadOnlySpan<int> owners)
    {
        for (var it = 0; it < 8; it++)
        {
            var jtj = new double[3, 3];
            var jtr = new double[3];
            foreach (var owner in owners)
            {
                var s = field.SampleSurface(owner, p)!.Value;
                double[] g = [s.Gradient.X, s.Gradient.Y, s.Gradient.Z];
                for (var r = 0; r < 3; r++)
                {
                    jtr[r] -= g[r] * s.Value;
                    for (var c = 0; c < 3; c++) jtj[r, c] += g[r] * g[c];
                }
            }
            var dx = SymmetricSolver.PseudoSolve(jtj, jtr, relativeCutoff: 1e-6);
            p = new Point3D(p.X + dx[0], p.Y + dx[1], p.Z + dx[2]);
        }
        return p;
    }

    // Newton steps along the exact gradient: puts smooth-region vertices on the surface. Where the gradient vanishes
    // (a blend whose two gradients cancel) there is no direction to step in, and the point stays.
    static Point3D Project(Field field, Point3D p)
    {
        for (var it = 0; it < 4; it++)
        {
            var s = field.Sample(p);
            if (s.Gradient.LengthSquared < 1e-24) break;
            p = p - s.Gradient * (s.Value / s.Gradient.LengthSquared);
        }
        return p;
    }

    // ---- Thin features ----

    /// <summary>
    /// Two surfaces with opposing normals through one finest cell, with material (or a gap) between them that they
    /// do not close off nearby, form a wall or gap thinner than the cell: one vertex per cell cannot represent it.
    /// Where the two surfaces meet within four cells they form a wedge whose sliver is that short: the cell vertices
    /// reproduce its edge. Only wedges sharper than about 24° have a longer sliver, and are reported.
    /// </summary>
    static void FindThin(Node node, Grid grid, ConcurrentBag<ThinSpot> spots)
    {
        foreach (var cavity in (bool[])[false, true])
        {
            var owners = new List<int>();
            Surfaces(node.Field!, owners, cavity);
            if (owners.Count >= 2) FindThin(node, grid, owners, spots);
        }
    }

    static void FindThin(Node node, Grid grid, List<int> owners, ConcurrentBag<ThinSpot> spots)
    {
        var field = node.Field!;
        var box = grid.Box(node);
        var c = box.Center;
        var cell = node.Size * grid.H;
        var reach = cell * Math.Sqrt(3) / 2;
        var near = owners.Select(o => (Owner: o, Sample: field.SampleSurface(o, c)!.Value))
            .Where(x => Math.Abs(x.Sample.Value) <= reach).ToList();
        for (var i = 0; i < near.Count; i++)
        for (var j = i + 1; j < near.Count; j++)
        {
            var (a, b) = (near[i].Owner, near[j].Owner);
            if (near[i].Sample.Gradient.Dot(near[j].Sample.Gradient) > -0.5) continue;
            var (pa, pb) = (OntoSurface(field, a, c), OntoSurface(field, b, c));
            var mid = new Point3D((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2, (pa.Z + pb.Z) / 2);
            if (!Inside(box, mid, 0)) continue;
            // Both must bound the part where they are: a lattice runs on past the faces that cut it.
            if (Math.Abs(field.Value(pa)) > 1e-6 || Math.Abs(field.Value(pb)) > 1e-6) continue;
            var (va, vb, vf) = (field.SampleSurface(a, mid)!.Value.Value, field.SampleSurface(b, mid)!.Value.Value, field.Value(mid));
            if (!(va < 0 && vb < 0 && vf < 0) && !(va > 0 && vb > 0 && vf > 0)) continue;
            var q = Refine(field, c, [a, b]);
            var meet = Math.Abs(field.SampleSurface(a, q)!.Value.Value) + Math.Abs(field.SampleSurface(b, q)!.Value.Value) < 1e-6;
            if (meet && Inside(box, q, Reach * cell)) continue;
            spots.Add(new ThinSpot(Math.Min(a, b), Math.Max(a, b), mid, (pa - pb).Length));
        }
    }

    /// <summary>
    /// The owners a cell's field can show, as the thin-spot search pairs them: a shell gives its outside only. With
    /// <paramref name="cavity"/> instead the inner faces of shells holding a lattice (the lattice is its own inner face),
    /// paired among themselves: the lattice meets the inner faces, and the skin keeps both from the outside.
    /// </summary>
    static void Surfaces(Field f, List<int> owners, bool cavity = false)
    {
        void Add(int id)
        {
            if (!cavity && !owners.Contains(id)) owners.Add(id);
        }
        switch (f)
        {
            case LeafField l:
                Add(l.Surface.Id);
                break;
            case MaxField m:
                foreach (var c in m.Children) Surfaces(c, owners, cavity);
                break;
            case MinField m:
                foreach (var c in m.Children) Surfaces(c, owners, cavity);
                break;
            case NegateField n:
                Surfaces(n.Child, owners, cavity);
                break;
            case RoundedEdgeField r:
                Surfaces(r.A, owners, cavity);
                Surfaces(r.B, owners, cavity);
                break;
            case ShellField s when !cavity:
                Surfaces(s.Outer, owners);
                break;
            case ShellField s when s.InnerOf.Any(kv => kv.Key == kv.Value):
                var child = new List<int>();
                Surfaces(s.Child, child);
                foreach (var o in child)
                    if (s.InnerOf.TryGetValue(o, out var inner) && !owners.Contains(inner)) owners.Add(inner);
                break;
            case ProfileField p:
                foreach (var l in p.Leaves) Add(l.Surface.Id);
                break;
            case BlendField b:
                Surfaces(b.A, owners, cavity);
                Surfaces(b.B, owners, cavity);
                break;
            case LoopEdgeField l:
                Surfaces(l.Cap, owners, cavity);
                Surfaces(l.Side, owners, cavity);
                break;
            case TapeField t:
                t.Tape.Surfaces(owners, cavity);
                break;
            case LatticeField l:
                Add(l.Surface.Id);
                break;
        }
    }

    static Point3D OntoSurface(Field field, int owner, Point3D p)
    {
        for (var it = 0; it < 4; it++)
        {
            var s = field.SampleSurface(owner, p)!.Value;
            p = p - s.Gradient * (s.Value / s.Gradient.LengthSquared);
        }
        return p;
    }

    static bool Inside(Box3D b, Point3D p, double margin) =>
        p.X >= b.Min.X - margin && p.X <= b.Max.X + margin
        && p.Y >= b.Min.Y - margin && p.Y <= b.Max.Y + margin
        && p.Z >= b.Min.Z - margin && p.Z <= b.Max.Z + margin;

    /// <summary>
    /// The cell lattice: node index i sits at <c>origin + (offset + i)·h</c>. Unsnapped, the origin is the part's
    /// corner less a margin and the offset zero. Snapped, the origin is fixed in the world and only the integer offset
    /// follows the part, so a region keeps its exact coordinates when an edit moves the part's bounding box.
    /// </summary>
    internal sealed class Grid(Point3D origin, double h, int nx, int ny, int nz, (int I, int J, int K) offset = default)
    {
        public int Nx => nx;
        public int Ny => ny;
        public int Nz => nz;
        public double H => h;
        public Point3D Origin => origin;
        public (int I, int J, int K) Offset => offset;

        /// <summary>Smallest power of two holding the grid: the octree root's size.</summary>
        public int RootSize { get; } = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(nx, Math.Max(ny, nz)));

        /// <summary>Side of a region in cells.</summary>
        public int RegionSize => RegionSizeFor(RootSize);

        /// <summary>At least 16 cells, so a region's own work outweighs its border; no more than 32 along the root.</summary>
        static int RegionSizeFor(int root) => Math.Min(root, Math.Max(16, root / 32));

        public int UnitDepth => System.Numerics.BitOperations.Log2((uint)(RootSize / RegionSize));

        /// <summary>Two cells of margin round the part, on a lattice shifted off round numbers.</summary>
        public static Grid Around(Point3D min, Point3D max, double h, bool snap)
        {
            int Count(double from, double to) => (int)Math.Ceiling((to - from) / h) + 2;
            if (!snap)
            {
                var origin = new Point3D(min.X - (2 + GridShift) * h, min.Y - (2 + GridShift) * h, min.Z - (2 + GridShift) * h);
                return new Grid(origin, h, Count(origin.X, max.X), Count(origin.Y, max.Y), Count(origin.Z, max.Z));
            }
            // The lattice starts on a multiple of the region size; a larger grid can mean larger regions, so repeat.
            var shift = new Point3D(-GridShift * h, -GridShift * h, -GridShift * h);
            for (var size = 16; ; )
            {
                int Snap(double m) => (int)Math.Floor((m / h - 2) / size) * size;
                var o = (Snap(min.X), Snap(min.Y), Snap(min.Z));
                var g = new Grid(shift, h, Count(shift.X + o.Item1 * h, max.X), Count(shift.Y + o.Item2 * h, max.Y), Count(shift.Z + o.Item3 * h, max.Z), o);
                if (g.RegionSize <= size) return g;
                size = g.RegionSize;
            }
        }

        /// <summary>
        /// The same lattice with cells 2^<paramref name="levels"/> times smaller: every node of this grid is a node of
        /// the refined one, 2^levels times as large.
        /// </summary>
        public Grid Refined(int levels)
        {
            var m = 1 << levels;
            return levels == 0 ? this : new Grid(origin, h / m, nx * m, ny * m, nz * m, (offset.I * m, offset.J * m, offset.K * m));
        }

        public Point3D Position(int i, int j, int k) => new(origin.X + (offset.I + i) * h, origin.Y + (offset.J + j) * h, origin.Z + (offset.K + k) * h);

        /// <summary>The closed box of a node, grown by <paramref name="margin"/> cells on every side.</summary>
        public Box3D Box(Node n, int margin = 0) => Box((n.I, n.J, n.K, n.Size), margin);

        public Box3D Box((int I, int J, int K, int Size) n, int margin = 0) =>
            new(Position(n.I - margin, n.J - margin, n.K - margin), Position(n.I + n.Size + margin, n.J + n.Size + margin, n.K + n.Size + margin));

        /// <summary>The region a leaf belongs to: its ancestor of region size, or the leaf itself if it is larger.</summary>
        public (int I, int J, int K, int Size) UnitOf(Node n)
        {
            var s = RegionSize;
            return n.Size >= s ? (n.I, n.J, n.K, n.Size) : (n.I / s * s, n.J / s * s, n.K / s * s, s);
        }
    }
}
