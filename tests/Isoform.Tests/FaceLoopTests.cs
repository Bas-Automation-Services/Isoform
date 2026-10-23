using BasAutomation.Geometry;
using Isoform.Meshing;

namespace Isoform.Tests;

/// <summary>
/// T1 and T2 on small hand-made faces: boundary loops (outer, holes, a face pinched to a point split there) and the
/// ear clipping that triangulates them (holes, collinear points, a hole touching its outer loop, exact orientation).
/// </summary>
public class FaceLoopTests
{
    /// <summary>A planar face of unit quads at the given cells of a grid, anticlockwise seen from +z.</summary>
    static Mesh Grid(int n, params (int I, int J)[] cells)
    {
        var vertices = new List<Point3D>();
        for (var j = 0; j <= n; j++)
            for (var i = 0; i <= n; i++) vertices.Add(new Point3D(i, j, 0));
        int V(int i, int j) => j * (n + 1) + i;
        return new Mesh(vertices, [.. cells.Select(c => new Quad(V(c.I, c.J), V(c.I + 1, c.J), V(c.I + 1, c.J + 1), V(c.I, c.J + 1), 0))]);
    }

    static IEnumerable<(int, int)> All(int n) => Enumerable.Range(0, n * n).Select(k => (k % n, k / n));

    static double Area(Mesh mesh, int[] loop) =>
        Enumerable.Range(0, loop.Length).Sum(i =>
        {
            var (a, b) = (mesh.Vertices[loop[i]], mesh.Vertices[loop[(i + 1) % loop.Length]]);
            return a.X * b.Y - b.X * a.Y;
        }) / 2;

    static List<int[]> LoopsOf(Mesh mesh) => FaceSimplifier.Loops(mesh, [.. Enumerable.Repeat(0, mesh.Quads.Count)])[0]!;

    [Fact]
    public void A_face_with_a_hole_has_an_anticlockwise_outer_loop_and_a_clockwise_hole()
    {
        var mesh = Grid(3, [.. All(3).Where(c => c != (1, 1))]);

        var loops = LoopsOf(mesh).OrderByDescending(l => l.Length).ToList();

        loops.Select(l => l.Length).Should().Equal(12, 4);
        Area(mesh, loops[0]).Should().Be(9);
        Area(mesh, loops[1]).Should().Be(-1);
    }

    [Fact]
    public void Two_holes_that_touch_in_a_point_are_two_loops()
    {
        // The holes at (1, 1) and (2, 2) share the grid point (2, 2): one boundary walk comes through it twice.
        var mesh = Grid(4, [.. All(4).Where(c => c != (1, 1) && c != (2, 2))]);

        var loops = LoopsOf(mesh).OrderByDescending(l => l.Length).ToList();

        loops.Select(l => l.Length).Should().Equal(16, 4, 4);
        loops.Should().OnlyContain(l => l.Distinct().Count() == l.Length, "no loop passes a vertex twice");
        loops.Skip(1).Select(l => Area(mesh, l)).Should().Equal(-1, -1);
        var pinch = 2 * 5 + 2;
        loops[1].Should().Contain(pinch);
        loops[2].Should().Contain(pinch);
    }

    [Fact]
    public void Two_pieces_that_meet_in_a_point_are_two_outer_loops()
    {
        var mesh = Grid(2, (0, 0), (1, 1));

        var loops = LoopsOf(mesh);

        loops.Should().HaveCount(2);
        loops.Select(l => Area(mesh, l)).Should().Equal(1, 1);
        loops[0].Intersect(loops[1]).Should().Equal(4);
    }

    static void Check(double[] x, double[] y, int[][] loops, int[] triangles)
    {
        var total = 0.0;
        for (var t = 0; t < triangles.Length; t += 3)
        {
            var (a, b, c) = (triangles[t], triangles[t + 1], triangles[t + 2]);
            PolygonTriangulation.Orient(x[a], y[a], x[b], y[b], x[c], y[c]).Should().Be(1);
            total += ((x[b] - x[a]) * (y[c] - y[a]) - (y[b] - y[a]) * (x[c] - x[a])) / 2;
        }
        total.Should().BeApproximately(loops.Sum(l => Area(x, y, l)), 1e-9, "anticlockwise triangles of the polygon's area cover it once");
    }

    static double Area(double[] x, double[] y, int[] l) => Enumerable.Range(0, l.Length).Sum(i => x[l[i]] * y[l[(i + 1) % l.Length]] - x[l[(i + 1) % l.Length]] * y[l[i]]) / 2;

    [Fact]
    public void A_square_with_a_hole_triangulates()
    {
        double[] x = [0, 4, 4, 0, 1, 1, 3, 3], y = [0, 0, 4, 4, 1, 3, 3, 1];
        int[][] loops = [[0, 1, 2, 3], [4, 5, 6, 7]];

        Check(x, y, loops, PolygonTriangulation.Triangulate(x, y, loops)!);
    }

    [Fact]
    public void Collinear_points_on_an_edge_stay_corners_of_real_triangles()
    {
        // A rectangle with ten points along its bottom edge: every one is used, none in a flat triangle.
        var (x, y) = (new List<double> { 0 }, new List<double> { 0 });
        for (var i = 1; i <= 10; i++) (x, y) = ([.. x, i], [.. y, 0]);
        (x, y) = ([.. x, 11, 11, 0], [.. y, 0, 1, 1]);
        int[][] loops = [[.. Enumerable.Range(0, x.Count)]];

        Check([.. x], [.. y], loops, PolygonTriangulation.Triangulate([.. x], [.. y], loops, 1e-9)!);
    }

    [Fact]
    public void A_hole_touching_its_outer_loop_in_a_point_joins_it_there()
    {
        double[] x = [0, 4, 4, 0, 0, 2, 2], y = [0, 0, 4, 4, 2, 3, 1];
        int[][] loops = [[0, 1, 2, 3, 4], [4, 5, 6]];

        var triangles = PolygonTriangulation.Triangulate(x, y, loops, 1e-9)!;

        Check(x, y, loops, triangles);
        PolygonTriangulation.Triangulate(x, y, loops, 1e-9).Should().Equal(triangles, "deterministic");
    }

    [Fact]
    public void A_diagonal_that_must_stay_apart_is_not_drawn()
    {
        double[] x = [0, 1, 1, 0], y = [0, 0, 1, 1];
        int[][] loops = [[0, 1, 2, 3]];

        var triangles = PolygonTriangulation.Triangulate(x, y, loops, 0, (a, b) => (a, b) is (0, 2) or (2, 0))!;

        Check(x, y, loops, triangles);
        triangles.Chunk(3).Should().OnlyContain(t => !(t.Contains(0) && t.Contains(2)));
        PolygonTriangulation.Triangulate(x, y, loops, 0, (_, _) => true).Should().BeNull("a square needs a diagonal");
    }

    [Fact]
    public void Orientation_is_exact_where_floating_point_cannot_tell()
    {
        PolygonTriangulation.Orient(0.5, 0.5, 12, 12, 24, 24).Should().Be(0);
        PolygonTriangulation.Orient(0, 0, 1, 1, 2, 2 + Math.Pow(2, -50)).Should().Be(1);
        PolygonTriangulation.Orient(0.1, 0.1, 0.2, 0.2, 0.30000000000000004, 0.3).Should().Be(-1);
    }
}
