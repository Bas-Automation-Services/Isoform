using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Meshing;

/// <summary>A quad of the surface mesh, labelled with the surface owning the grid-edge crossing it is dual to.</summary>
public readonly record struct Quad(int A, int B, int C, int D, int Owner);

public readonly record struct Triangle(int A, int B, int C, int Owner);

public sealed class Mesh(IReadOnlyList<Point3D> vertices, IReadOnlyList<Quad> quads)
{
    public IReadOnlyList<Point3D> Vertices { get; } = vertices;
    public IReadOnlyList<Quad> Quads { get; } = quads;

    /// <summary>Each quad split along its shorter diagonal.</summary>
    public IEnumerable<Triangle> Triangles()
    {
        foreach (var q in Quads)
        {
            if ((Vertices[q.A] - Vertices[q.C]).LengthSquared <= (Vertices[q.B] - Vertices[q.D]).LengthSquared)
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
/// Dual contouring on a uniform grid. Crossings are found by bisection on the exact field, normals are the exact
/// field gradients, and each cell vertex minimises the quadric error of its crossings' tangent planes, so sharp
/// edges and corners are reproduced. Cells whose crossings share one owner are smooth: their vertex is projected
/// onto the surface. Output is deterministic: same field, bounds and cell size give the same mesh.
/// </summary>
public static class DualContouring
{
    // Shifts the grid off round numbers so grid points never land exactly on axis-aligned faces.
    const double GridShift = 0.31830988618379067;

    readonly record struct Crossing(Point3D Point, Vector3D Normal, int Owner);

    /// <param name="prune">
    /// Skip blocks of cells whose interval bounds prove they hold no surface. The mesh is identical either way;
    /// false evaluates every grid point and exists to test exactly that.
    /// </param>
    public static Mesh Build(Field field, Point3D min, Point3D max, double cellSize, Action<string, TimeSpan>? trace = null, bool prune = true)
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

        // Octree over cell ranges: a block whose bounds are certainly positive or negative has no sign change
        // on any grid edge inside its closed box, so it is skipped without evaluating a single point.
        var leaves = new List<(Block Block, Field Field)>();
        Subdivide(new Block(0, 0, 0, nx, ny, nz), field, grid, prune, leaves);
        Stage($"octree ({leaves.Count:N0} leaf blocks)");

        // Each leaf evaluates its own closed box of grid points and reports its sign-changing edges. It also gets
        // a field simplified for its box plus a two-cell margin, exact wherever its cells' vertices can move.
        var found = new List<(long Key, bool StartInside)>[leaves.Count];
        var near = new Field[leaves.Count];
        Parallel.For(0, leaves.Count, b =>
        {
            found[b] = SignChanges(leaves[b].Field, grid, leaves[b].Block);
            near[b] = prune ? field.Simplify(grid.Expanded(leaves[b].Block, 2)) : field;
        });
        // Every edge is reported by exactly one leaf, in leaf order. Pruning only removes leaves, so the order
        // (and the mesh) is the same with and without it.
        var edgeKeys = new List<(long Key, bool StartInside, int Leaf)>(found.Sum(f => f.Count));
        for (var b = 0; b < found.Length; b++)
            foreach (var (key, inside) in found[b]) edgeKeys.Add((key, inside, b));
        var edges = edgeKeys.Select(e => grid.Edge(e.Key)).ToList();
        Stage($"sign changes ({edges.Count:N0} edges)");

        var crossings = new Crossing[edges.Count];
        Parallel.For(0, edges.Count, e =>
        {
            var (axis, i, j, k) = edges[e];
            var p0 = grid.Position(i, j, k);
            var leafField = leaves[edgeKeys[e].Leaf].Field;
            var c = FindRoot(leafField, p0, p0 + Grid.Unit(axis) * h);
            var sample = leafField.Sample(c);
            crossings[e] = new(c, sample.Gradient.Normalized(), sample.Owner);
        });
        Stage("crossings (root finding)");

        // Each edge touches four cells. Sorting (cell, edge, slot) groups the crossings per cell, puts cells in
        // ascending order and keeps each cell's crossings in edge order.
        var pairs = new long[edges.Count * 4];
        for (var e = 0; e < edges.Count; e++)
        {
            var around = grid.CellsAround(edges[e]);
            for (var slot = 0; slot < 4; slot++) pairs[e * 4 + slot] = ((long)around[slot] << 32) | (uint)(e * 4 + slot);
        }
        Array.Sort(pairs);
        var cellStart = new List<int>();
        for (var k = 0; k < pairs.Length; k++)
            if (k == 0 || pairs[k] >> 32 != pairs[k - 1] >> 32) cellStart.Add(k);
        cellStart.Add(pairs.Length);
        var cellCount = cellStart.Count - 1;
        var vertexOfSlot = new int[pairs.Length];
        for (var c = 0; c < cellCount; c++)
            for (var k = cellStart[c]; k < cellStart[c + 1]; k++) vertexOfSlot[(int)(uint)pairs[k]] = c;

        var positions = new Point3D[cellCount];
        Parallel.For(0, cellCount, c =>
        {
            Span<Crossing> local = stackalloc Crossing[12];
            var n = 0;
            for (var k = cellStart[c]; k < cellStart[c + 1]; k++) local[n++] = crossings[(int)(uint)pairs[k] / 4];
            // The first (lowest) edge of the cell decides which leaf's local field it uses.
            var firstEdge = (int)(uint)pairs[cellStart[c]] / 4;
            positions[c] = CellVertex(near[edgeKeys[firstEdge].Leaf], grid, (int)(pairs[cellStart[c]] >> 32), local[..n]);
        });
        Stage($"cell vertices ({cellCount:N0} cells)");

        var quads = new List<Quad>(edges.Count);
        for (var e = 0; e < edges.Count; e++)
        {
            var (a, b, c, d) = (vertexOfSlot[e * 4], vertexOfSlot[e * 4 + 1], vertexOfSlot[e * 4 + 2], vertexOfSlot[e * 4 + 3]);
            // Wind so the quad faces outward: away from the inside end of the edge.
            quads.Add(edgeKeys[e].StartInside ? new Quad(a, b, c, d, crossings[e].Owner) : new Quad(d, c, b, a, crossings[e].Owner));
        }

        Stage($"quads ({quads.Count:N0})");
        return new Mesh(positions, quads);
    }

    readonly record struct Block(int I0, int J0, int K0, int I1, int J1, int K1);

    const int LeafCells = 4;

    // Certify only with a margin, so floating-point noise in a bound can never hide a real sign change.
    const double CertifyMargin = 1e-9;

    static void Subdivide(Block b, Field field, Grid grid, bool prune, List<(Block, Field)> leaves)
    {
        if (prune)
        {
            // Simplify first: the children are then bounded on a smaller tree.
            var box = new Box3D(grid.Position(b.I0, b.J0, b.K0), grid.Position(b.I1, b.J1, b.K1));
            field = field.Simplify(box);
            var bounds = field.Bounds(box);
            if (bounds.Lo > CertifyMargin || bounds.Hi < -CertifyMargin) return;
        }
        var (sx, sy, sz) = (b.I1 - b.I0, b.J1 - b.J0, b.K1 - b.K0);
        if (sx <= LeafCells && sy <= LeafCells && sz <= LeafCells)
        {
            leaves.Add((b, field));
            return;
        }
        if (sx >= sy && sx >= sz)
        {
            var m = b.I0 + sx / 2;
            Subdivide(b with { I1 = m }, field, grid, prune, leaves);
            Subdivide(b with { I0 = m }, field, grid, prune, leaves);
        }
        else if (sy >= sz)
        {
            var m = b.J0 + sy / 2;
            Subdivide(b with { J1 = m }, field, grid, prune, leaves);
            Subdivide(b with { J0 = m }, field, grid, prune, leaves);
        }
        else
        {
            var m = b.K0 + sz / 2;
            Subdivide(b with { K1 = m }, field, grid, prune, leaves);
            Subdivide(b with { K0 = m }, field, grid, prune, leaves);
        }
    }

    static List<(long, bool)> SignChanges(Field field, Grid grid, Block b)
    {
        var (sx, sy, sz) = (b.I1 - b.I0 + 1, b.J1 - b.J0 + 1, b.K1 - b.K0 + 1);
        var inside = new bool[sx * sy * sz];
        for (var k = 0; k < sz; k++)
        for (var j = 0; j < sy; j++)
        for (var i = 0; i < sx; i++)
            inside[(k * sy + j) * sx + i] = field.Value(grid.Position(b.I0 + i, b.J0 + j, b.K0 + k)) < 0;

        // An edge belongs to the block holding the cell at its start point, so each edge is reported once. A
        // skipped neighbour block has no sign change on the edges it owns: they lie in its closed box.
        var result = new List<(long, bool)>();
        for (var k = 0; k < sz - 1; k++)
        for (var j = 0; j < sy - 1; j++)
        for (var i = 0; i < sx - 1; i++)
        {
            var here = inside[(k * sy + j) * sx + i];
            var (gi, gj, gk) = (b.I0 + i, b.J0 + j, b.K0 + k);
            if (here != inside[(k * sy + j) * sx + i + 1]) result.Add((grid.EdgeKey(0, gi, gj, gk), here));
            if (here != inside[(k * sy + j + 1) * sx + i]) result.Add((grid.EdgeKey(1, gi, gj, gk), here));
            if (here != inside[((k + 1) * sy + j) * sx + i]) result.Add((grid.EdgeKey(2, gi, gj, gk), here));
        }
        return result;
    }

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
        for (var it = 0; it < 60; it++)
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

    static Point3D CellVertex(Field field, Grid grid, int cell, ReadOnlySpan<Crossing> crossings)
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
        return grid.ContainsLoosely(cell, v) ? v : mp;
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

    sealed class Grid(Point3D origin, double h, int nx, int ny, int nz)
    {
        public int Point(int i, int j, int k) => (k * (ny + 1) + j) * (nx + 1) + i;

        public Point3D Position(int i, int j, int k) => new(origin.X + i * h, origin.Y + j * h, origin.Z + k * h);

        /// <summary>Ordered like the dense scan: by grid point (k, j, i), then axis.</summary>
        public long EdgeKey(int axis, int i, int j, int k) => (long)Point(i, j, k) * 3 + axis;

        public (int Axis, int I, int J, int K) Edge(long key)
        {
            var point = (int)(key / 3);
            return ((int)(key % 3), point % (nx + 1), point / (nx + 1) % (ny + 1), point / ((nx + 1) * (ny + 1)));
        }

        int Cell(int i, int j, int k) => (k * ny + j) * nx + i;

        public static Vector3D Unit(int axis) => axis switch { 0 => new(1, 0, 0), 1 => new(0, 1, 0), _ => new(0, 0, 1) };

        /// <summary>The four cells sharing an edge, in counter-clockwise order around the edge's axis.</summary>
        public int[] CellsAround((int Axis, int I, int J, int K) e) => e.Axis switch
        {
            0 => [Cell(e.I, e.J - 1, e.K - 1), Cell(e.I, e.J, e.K - 1), Cell(e.I, e.J, e.K), Cell(e.I, e.J - 1, e.K)],
            1 => [Cell(e.I - 1, e.J, e.K - 1), Cell(e.I - 1, e.J, e.K), Cell(e.I, e.J, e.K), Cell(e.I, e.J, e.K - 1)],
            _ => [Cell(e.I - 1, e.J - 1, e.K), Cell(e.I, e.J - 1, e.K), Cell(e.I, e.J, e.K), Cell(e.I - 1, e.J, e.K)],
        };

        /// <summary>The closed box of a block, grown by <paramref name="margin"/> cells on every side.</summary>
        public Box3D Expanded(Block b, int margin) =>
            new(Position(b.I0 - margin, b.J0 - margin, b.K0 - margin), Position(b.I1 + margin, b.J1 + margin, b.K1 + margin));

        public bool ContainsLoosely(int cell, Point3D p)
        {
            var i = cell % nx;
            var j = cell / nx % ny;
            var k = cell / (nx * ny);
            var lo = Position(i, j, k);
            return p.X >= lo.X - h / 2 && p.X <= lo.X + 1.5 * h
                && p.Y >= lo.Y - h / 2 && p.Y <= lo.Y + 1.5 * h
                && p.Z >= lo.Z - h / 2 && p.Z <= lo.Z + 1.5 * h;
        }
    }
}
