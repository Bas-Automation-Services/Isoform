using BasAutomation.Geometry;

namespace Isoform.Meshing;

/// <summary>Bounding-volume hierarchy over triangles for exact point-to-mesh distance queries.</summary>
public sealed class TriangleBvh
{
    readonly Point3D[] _a, _b, _c;
    readonly List<Node> _nodes = [];
    readonly int[] _order;

    record struct Node(Point3D Min, Point3D Max, int Left, int Right, int Start, int Count);

    public TriangleBvh(IReadOnlyList<Point3D> vertices, IReadOnlyList<Triangle> triangles)
    {
        _a = triangles.Select(t => vertices[t.A]).ToArray();
        _b = triangles.Select(t => vertices[t.B]).ToArray();
        _c = triangles.Select(t => vertices[t.C]).ToArray();
        _order = Enumerable.Range(0, triangles.Count).ToArray();
        if (triangles.Count > 0) Build(0, triangles.Count);
    }

    public double Distance(Point3D p)
    {
        var best = double.MaxValue;
        if (_nodes.Count > 0) Search(0, p, ref best);
        return Math.Sqrt(best);
    }

    int Build(int start, int count)
    {
        var (min, max) = BoundsOf(start, count);
        var index = _nodes.Count;
        _nodes.Add(default);
        if (count <= 4)
        {
            _nodes[index] = new Node(min, max, -1, -1, start, count);
            return index;
        }

        // Median split on the longest axis of the centroids.
        var extent = max - min;
        var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        Array.Sort(_order, start, count, Comparer<int>.Create((i, j) => Centroid(i, axis).CompareTo(Centroid(j, axis))));
        var half = count / 2;
        var left = Build(start, half);
        var right = Build(start + half, count - half);
        _nodes[index] = new Node(min, max, left, right, start, count);
        return index;
    }

    void Search(int index, Point3D p, ref double best)
    {
        var node = _nodes[index];
        if (BoxDistanceSquared(node, p) >= best) return;
        if (node.Left < 0)
        {
            for (var k = node.Start; k < node.Start + node.Count; k++)
            {
                var t = _order[k];
                best = Math.Min(best, (ClosestOnTriangle(p, _a[t], _b[t], _c[t]) - p).LengthSquared);
            }
            return;
        }
        var (first, second) = BoxDistanceSquared(_nodes[node.Left], p) <= BoxDistanceSquared(_nodes[node.Right], p)
            ? (node.Left, node.Right) : (node.Right, node.Left);
        Search(first, p, ref best);
        Search(second, p, ref best);
    }

    (Point3D, Point3D) BoundsOf(int start, int count)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
        double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        for (var k = start; k < start + count; k++)
        {
            var t = _order[k];
            foreach (var v in new[] { _a[t], _b[t], _c[t] })
            {
                x0 = Math.Min(x0, v.X); y0 = Math.Min(y0, v.Y); z0 = Math.Min(z0, v.Z);
                x1 = Math.Max(x1, v.X); y1 = Math.Max(y1, v.Y); z1 = Math.Max(z1, v.Z);
            }
        }
        return (new Point3D(x0, y0, z0), new Point3D(x1, y1, z1));
    }

    double Centroid(int t, int axis) => axis switch
    {
        0 => _a[t].X + _b[t].X + _c[t].X,
        1 => _a[t].Y + _b[t].Y + _c[t].Y,
        _ => _a[t].Z + _b[t].Z + _c[t].Z,
    };

    static double BoxDistanceSquared(Node n, Point3D p)
    {
        var dx = Math.Max(0, Math.Max(n.Min.X - p.X, p.X - n.Max.X));
        var dy = Math.Max(0, Math.Max(n.Min.Y - p.Y, p.Y - n.Max.Y));
        var dz = Math.Max(0, Math.Max(n.Min.Z - p.Z, p.Z - n.Max.Z));
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>Closest point on triangle abc to p (Ericson, Real-Time Collision Detection, 5.1.5).</summary>
    public static Point3D ClosestOnTriangle(Point3D p, Point3D a, Point3D b, Point3D c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        var d1 = ab.Dot(ap);
        var d2 = ac.Dot(ap);
        if (d1 <= 0 && d2 <= 0) return a;

        var bp = p - b;
        var d3 = ab.Dot(bp);
        var d4 = ac.Dot(bp);
        if (d3 >= 0 && d4 <= d3) return b;

        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));

        var cp = p - c;
        var d5 = ab.Dot(cp);
        var d6 = ac.Dot(cp);
        if (d6 >= 0 && d5 <= d6) return c;

        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));

        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

        var denom = 1 / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }
}
