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

    public static Mesh Build(Field field, Point3D min, Point3D max, double cellSize, Action<string, TimeSpan>? trace = null)
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

        var values = new double[(nx + 1) * (ny + 1) * (nz + 1)];
        Parallel.For(0, nz + 1, k =>
        {
            for (var j = 0; j <= ny; j++)
            for (var i = 0; i <= nx; i++)
                values[grid.Point(i, j, k)] = field.Value(grid.Position(i, j, k));
        });

        Stage($"grid values ({values.Length:N0} points)");

        // Sign-changing grid edges, in a fixed order: (axis, i, j, k).
        var edges = new List<(int Axis, int I, int J, int K)>();
        for (var k = 0; k <= nz; k++)
        for (var j = 0; j <= ny; j++)
        for (var i = 0; i <= nx; i++)
        {
            var inside = values[grid.Point(i, j, k)] < 0;
            if (i < nx && inside != values[grid.Point(i + 1, j, k)] < 0) edges.Add((0, i, j, k));
            if (j < ny && inside != values[grid.Point(i, j + 1, k)] < 0) edges.Add((1, i, j, k));
            if (k < nz && inside != values[grid.Point(i, j, k + 1)] < 0) edges.Add((2, i, j, k));
        }

        Stage($"sign changes ({edges.Count:N0} edges)");
        var crossings = new Crossing[edges.Count];
        Parallel.For(0, edges.Count, e =>
        {
            var (axis, i, j, k) = edges[e];
            var p0 = grid.Position(i, j, k);
            var p1 = p0 + Grid.Unit(axis) * h;
            var c = Bisect(field, p0, p1, values[grid.Point(i, j, k)] < 0);
            var s = field.Sample(c);
            crossings[e] = new(c, s.Gradient.Normalized(), s.Owner);
        });

        Stage("crossings (bisection)");

        // Crossings per cell (each edge touches up to four cells).
        var cellCrossings = new Dictionary<int, List<int>>();
        for (var e = 0; e < edges.Count; e++)
            foreach (var cell in grid.CellsAround(edges[e]))
            {
                if (!cellCrossings.TryGetValue(cell, out var list)) cellCrossings[cell] = list = [];
                list.Add(e);
            }

        var cells = cellCrossings.Keys.Order().ToArray();
        var positions = new Point3D[cells.Length];
        Parallel.For(0, cells.Length, c =>
            positions[c] = CellVertex(field, grid, cells[c], cellCrossings[cells[c]].Select(e => crossings[e]).ToList()));
        Stage($"cell vertices ({cells.Length:N0} cells)");
        var vertexOfCell = new Dictionary<int, int>(cells.Length);
        for (var c = 0; c < cells.Length; c++) vertexOfCell[cells[c]] = c;

        var quads = new List<Quad>(edges.Count);
        for (var e = 0; e < edges.Count; e++)
        {
            var around = grid.CellsAround(edges[e]).Select(c => vertexOfCell[c]).ToArray();
            var (axis, i, j, k) = edges[e];
            // Wind so the quad faces outward: away from the inside end of the edge.
            var startInside = values[grid.Point(i, j, k)] < 0;
            quads.Add(startInside
                ? new Quad(around[0], around[1], around[2], around[3], crossings[e].Owner)
                : new Quad(around[3], around[2], around[1], around[0], crossings[e].Owner));
        }

        Stage($"quads ({quads.Count:N0})");
        return new Mesh(positions, quads);
    }

    static Point3D Bisect(Field field, Point3D a, Point3D b, bool aInside)
    {
        for (var it = 0; it < 60; it++)
        {
            var m = new Point3D((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
            if (field.Value(m) < 0 == aInside) a = m; else b = m;
            if ((b - a).LengthSquared == 0) break;
        }
        return new Point3D((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
    }

    static Point3D CellVertex(Field field, Grid grid, int cell, List<Crossing> crossings)
    {
        var mass = new Vector3D(0, 0, 0);
        foreach (var c in crossings) mass += c.Point.AsVector();
        var m = (mass / crossings.Count);
        var mp = new Point3D(m.X, m.Y, m.Z);

        if (crossings.All(c => c.Owner == crossings[0].Owner))
            return Project(field, mp);

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
        var v = Refine(field, new Point3D(mp.X + x[0], mp.Y + x[1], mp.Z + x[2]), crossings.Select(c => c.Owner).Distinct().ToList());
        // Tangent (G1) junctions leave the along-face position undetermined; land on the solid itself.
        v = Project(field, v);
        return grid.ContainsLoosely(cell, v) ? v : mp;
    }

    // The QEF uses tangent planes; Gauss-Newton on the owner surfaces themselves moves the vertex onto the
    // real (possibly curved) edge or corner.
    static Point3D Refine(Field field, Point3D p, List<int> owners)
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

        int Cell(int i, int j, int k) => (k * ny + j) * nx + i;

        public static Vector3D Unit(int axis) => axis switch { 0 => new(1, 0, 0), 1 => new(0, 1, 0), _ => new(0, 0, 1) };

        /// <summary>The four cells sharing an edge, in counter-clockwise order around the edge's axis.</summary>
        public int[] CellsAround((int Axis, int I, int J, int K) e) => e.Axis switch
        {
            0 => [Cell(e.I, e.J - 1, e.K - 1), Cell(e.I, e.J, e.K - 1), Cell(e.I, e.J, e.K), Cell(e.I, e.J - 1, e.K)],
            1 => [Cell(e.I - 1, e.J, e.K - 1), Cell(e.I - 1, e.J, e.K), Cell(e.I, e.J, e.K), Cell(e.I, e.J, e.K - 1)],
            _ => [Cell(e.I - 1, e.J - 1, e.K), Cell(e.I, e.J - 1, e.K), Cell(e.I, e.J, e.K), Cell(e.I - 1, e.J, e.K)],
        };

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
