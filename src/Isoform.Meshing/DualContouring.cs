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

public sealed class Mesh(IReadOnlyList<Point3D> vertices, IReadOnlyList<Quad> quads, IReadOnlyList<ThinSpot>? thinSpots = null)
{
    public IReadOnlyList<Point3D> Vertices { get; } = vertices;
    public IReadOnlyList<Quad> Quads { get; } = quads;
    public IReadOnlyList<ThinSpot> ThinSpots { get; } = thinSpots ?? [];

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
/// crossings' tangent planes, so sharp edges and corners are reproduced. Output is deterministic.
/// </summary>
public static class DualContouring
{
    // Shifts the grid off round numbers so grid points never land exactly on axis-aligned faces.
    const double GridShift = 0.31830988618379067;

    // Certify only with a margin, so floating-point noise in a bound can never hide a real sign change.
    const double CertifyMargin = 1e-9;

    // Octree levels below the root that are built and traversed in parallel. Flat parts fill only a few of the
    // top-level nodes, so this goes deeper than the core count alone would suggest.
    const int ParallelDepth = 4;

    readonly record struct Crossing(Point3D Point, Vector3D Normal, int Owner);

    /// <summary>A sign-changing minimal edge, the four leaves around it in counter-clockwise order, and the smallest.</summary>
    readonly record struct Hit(Point3D P0, Point3D P1, bool StartInside, Node N0, Node N1, Node N2, Node N3, Node Smallest);

    sealed class Node(int i, int j, int k, int size)
    {
        public readonly int I = i, J = j, K = k, Size = size;
        public Node[]? Children;

        /// <summary>Set on leaves that hold surface: the field simplified for the node's closed box.</summary>
        public Field? Field;

        /// <summary>A finest leaf holding more than one surface.</summary>
        public bool Multi;

        public int Id = -1;
        public bool IsLeaf => Children is null;
        public bool IsEmpty => Children is null && Field is null;
    }

    sealed record Context(Grid Grid, bool Prune, bool Adaptive, double ChordTolerance);

    /// <param name="chordTolerance">Largest distance a polygon may stray from a curved surface away from edges.</param>
    /// <param name="adaptive">False refines every surface cell to <paramref name="cellSize"/>.</param>
    /// <param name="prune">
    /// Skip cells whose interval bounds prove they hold no surface, and simplify the field per cell. With
    /// <paramref name="adaptive"/> false the mesh is identical either way; false exists to test exactly that.
    /// </param>
    public static Mesh Build(Field field, Point3D min, Point3D max, double cellSize, double chordTolerance,
        Action<string, TimeSpan>? trace = null, bool adaptive = true, bool prune = true)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Stage(string name)
        {
            trace?.Invoke(name, clock.Elapsed);
            clock.Restart();
        }

        var h = cellSize;
        var origin = new Point3D(min.X - (2 + GridShift) * h, min.Y - (2 + GridShift) * h, min.Z - (2 + GridShift) * h);
        var nx = (int)Math.Ceiling((max.X - origin.X) / h) + 2;
        var ny = (int)Math.Ceiling((max.Y - origin.Y) / h) + 2;
        var nz = (int)Math.Ceiling((max.Z - origin.Z) / h) + 2;
        var grid = new Grid(origin, h, nx, ny, nz);
        var ctx = new Context(grid, prune, adaptive && prune, chordTolerance);

        var rootSize = 1;
        while (rootSize < Math.Max(nx, Math.Max(ny, nz))) rootSize *= 2;
        var root = new Node(0, 0, 0, rootSize);
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
        Stage("crossings (root finding)");

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
        var vertexOfCell = new int[leaves.Count];
        for (var v = 0; v < cellCount; v++) vertexOfCell[(int)(pairs[cellStart[v]] >> 32)] = v;

        var positions = new Point3D[cellCount];
        Parallel.For(0, cellCount, v =>
        {
            var n = cellStart[v + 1] - cellStart[v];
            Span<Crossing> local = n <= 48 ? stackalloc Crossing[48] : new Crossing[n];
            for (var k = 0; k < n; k++) local[k] = crossings[(int)(uint)pairs[cellStart[v] + k]];
            var node = leaves[(int)(pairs[cellStart[v]] >> 32)];
            // Where several surfaces meet the vertex may sit up to half a cell outside the node, so it gets a field
            // exact one cell size around it. A vertex on one surface is projected onto that surface.
            var near = !prune ? field : SingleOwner(local[..n]) ? node.Field! : field.Simplify(grid.Box(node, node.Size));
            positions[v] = CellVertex(near, grid, node, local[..n]);
        });
        Stage($"cell vertices ({cellCount:N0} cells)");

        var quads = new List<Quad>(hits.Count);
        Span<int> ring = stackalloc int[4];
        Span<int> around = stackalloc int[4];
        for (var e = 0; e < hits.Count; e++)
        {
            var hit = hits[e];
            (around[0], around[1], around[2], around[3]) = (vertexOfCell[hit.N0.Id], vertexOfCell[hit.N1.Id], vertexOfCell[hit.N2.Id], vertexOfCell[hit.N3.Id]);
            // Drop a repeated neighbour (duplicates are adjacent around the edge), keeping the cyclic order.
            var count = 0;
            for (var q = 0; q < 4; q++)
                if (around[q] != around[(q + 3) % 4]) ring[count++] = around[q];
            var owner = crossings[e].Owner;
            // Wind so the polygon faces outward: away from the inside end of the edge.
            if (count == 4)
                quads.Add(hit.StartInside ? new Quad(ring[0], ring[1], ring[2], ring[3], owner) : new Quad(ring[3], ring[2], ring[1], ring[0], owner));
            else
                quads.Add(hit.StartInside ? new Quad(ring[0], ring[1], ring[2], ring[2], owner) : new Quad(ring[2], ring[1], ring[0], ring[0], owner));
        }
        Stage($"polygons ({quads.Count:N0})");

        var spots = new ConcurrentBag<ThinSpot>();
        if (prune) Parallel.ForEach(leaves.Where(l => l.Multi), node => FindThin(node, grid, spots));
        var thin = spots.OrderBy(s => s.OwnerA).ThenBy(s => s.OwnerB).ThenBy(s => s.Location.X).ThenBy(s => s.Location.Y).ThenBy(s => s.Location.Z).ToList();
        Stage($"thin spots ({thin.Count:N0})");

        return new Mesh(positions, quads, thin);
    }

    static void Subdivide(Node node, Field field, Context ctx, int depth)
    {
        var g = ctx.Grid;
        if (node.I >= g.Nx || node.J >= g.Ny || node.K >= g.Nz) return; // beyond the part's margin: empty
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
        if (node.Size == 1 || ctx.Adaptive && patch is { } r && node.Size * g.H <= MaxCurvedCell(r, ctx.ChordTolerance))
        {
            node.Field = field;
            node.Multi = ctx.Prune && node.Size == 1 && patch is null;
            return;
        }
        var half = node.Size / 2;
        var children = new Node[8];
        for (var c = 0; c < 8; c++)
            children[c] = new Node(node.I + (c & 1) * half, node.J + (c >> 1 & 1) * half, node.K + (c >> 2) * half, half);
        node.Children = children;
        if (depth < ParallelDepth) Parallel.For(0, 8, c => Subdivide(children[c], field, ctx, depth + 1));
        else foreach (var child in children) Subdivide(child, field, ctx, depth + 1);
    }

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
        if (depth < ParallelDepth)
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
        var a = ctx.Grid.Position(p[0], p[1], p[2]);
        p[e] += s.Size;
        var b = ctx.Grid.Position(p[0], p[1], p[2]);

        // The smallest leaf's field is exact on its closed box, so every leaf agrees on these signs.
        var inside = s.Field!.Value(a) < 0;
        if (inside != s.Field.Value(b) < 0) output.Add(new Hit(a, b, inside, n0, n1, n2, n3, s));
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

    static bool SingleOwner(ReadOnlySpan<Crossing> crossings)
    {
        foreach (var c in crossings)
            if (c.Owner != crossings[0].Owner) return false;
        return true;
    }

    static Point3D CellVertex(Field field, Grid grid, Node node, ReadOnlySpan<Crossing> crossings)
    {
        var mass = new Vector3D(0, 0, 0);
        foreach (var c in crossings) mass += c.Point.AsVector();
        var m = mass / crossings.Length;
        var mp = new Point3D(m.X, m.Y, m.Z);

        // Distinct owners in order of first appearance.
        Span<int> owners = stackalloc int[crossings.Length];
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
        var v = Refine(field, new Point3D(mp.X + x[0], mp.Y + x[1], mp.Z + x[2]), owners[..ownerCount]);
        // Tangent (G1) junctions leave the along-face position undetermined; land on the solid itself.
        v = Project(field, v);
        return Inside(grid.Box(node), v, node.Size * grid.H / 2) ? v : mp;
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

    // Newton steps along the exact gradient: puts smooth-region vertices on the surface.
    static Point3D Project(Field field, Point3D p)
    {
        for (var it = 0; it < 4; it++)
        {
            var s = field.Sample(p);
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
        var field = node.Field!;
        var owners = new List<int>();
        Surfaces(field, owners);
        if (owners.Count < 2) return;
        var box = grid.Box(node);
        var c = box.Center;
        var reach = grid.H * Math.Sqrt(3) / 2;
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
            var (va, vb, vf) = (field.SampleSurface(a, mid)!.Value.Value, field.SampleSurface(b, mid)!.Value.Value, field.Value(mid));
            if (!(va < 0 && vb < 0 && vf < 0) && !(va > 0 && vb > 0 && vf > 0)) continue;
            var q = Refine(field, c, [a, b]);
            var meet = Math.Abs(field.SampleSurface(a, q)!.Value.Value) + Math.Abs(field.SampleSurface(b, q)!.Value.Value) < 1e-6;
            if (meet && Inside(box, q, 4 * grid.H)) continue;
            spots.Add(new ThinSpot(Math.Min(a, b), Math.Max(a, b), mid, (pa - pb).Length));
        }
    }

    static void Surfaces(Field f, List<int> owners)
    {
        switch (f)
        {
            case LeafField l:
                if (!owners.Contains(l.Surface.Id)) owners.Add(l.Surface.Id);
                break;
            case MaxField m:
                foreach (var c in m.Children) Surfaces(c, owners);
                break;
            case MinField m:
                foreach (var c in m.Children) Surfaces(c, owners);
                break;
            case NegateField n:
                Surfaces(n.Child, owners);
                break;
            case RoundedEdgeField r:
                Surfaces(r.A, owners);
                Surfaces(r.B, owners);
                break;
            case ShellField s:
                Surfaces(s.Child, owners);
                break;
            case ProfileField p:
                foreach (var l in p.Leaves) Surfaces(l, owners);
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

    sealed class Grid(Point3D origin, double h, int nx, int ny, int nz)
    {
        public int Nx => nx;
        public int Ny => ny;
        public int Nz => nz;
        public double H => h;

        public Point3D Position(int i, int j, int k) => new(origin.X + i * h, origin.Y + j * h, origin.Z + k * h);

        /// <summary>The closed box of a node, grown by <paramref name="margin"/> cells on every side.</summary>
        public Box3D Box(Node n, int margin = 0) =>
            new(Position(n.I - margin, n.J - margin, n.K - margin), Position(n.I + n.Size + margin, n.J + n.Size + margin, n.K + n.Size + margin));
    }
}
