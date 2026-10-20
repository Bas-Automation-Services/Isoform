using BasAutomation.Geometry;
using Isoform.Geometry;

namespace Isoform.Step;

/// <summary>
/// Surface parameters as ISO 10303-42 defines them, in the placement the STEP writer gives every surface (origin,
/// z = Axis, x = <see cref="SurfaceIntersection.Perpendicular"/> of the axis), and the images of edges in them:
/// <list type="bullet">
/// <item>plane: (x, y) in the plane;</item>
/// <item>cylinder and cone: u the angle round the axis, v the height along it (a cone's apex at v = 0);</item>
/// <item>sphere: u round the axis, v the latitude;</item>
/// <item>torus: u round the axis, v round the tube.</item>
/// </list>
/// </summary>
public static class SurfaceParameters
{
    const int Degree = 9;

    /// <summary>The placement axes of a surface.</summary>
    public static (Vector3D X, Vector3D Y, Vector3D Z) Axes(Surface s)
    {
        var x = SurfaceIntersection.Perpendicular(s.Axis);
        return (x, s.Axis.Cross(x), s.Axis);
    }

    /// <summary>(u, v) of a point on the surface, u in (−π, π]; u is NaN on the axis, where any angle is right.</summary>
    public static (double U, double V) Of(Surface s, Point3D p)
    {
        var (x, y, z) = Axes(s);
        var d = p - s.Origin;
        var (a, b, h) = (x.Dot(d), y.Dot(d), z.Dot(d));
        var rho = Math.Sqrt(a * a + b * b);
        var u = rho <= 1e-12 * (1 + Math.Abs(h)) ? double.NaN : Math.Atan2(b, a);
        return s.Kind switch
        {
            SurfaceKind.Plane => (a, b),
            SurfaceKind.Cylinder or SurfaceKind.Cone => (u, h),
            SurfaceKind.Sphere => (u, Math.Atan2(h, rho)),
            SurfaceKind.Torus => (u, Math.Atan2(h, rho - s.Radius)),
            _ => throw new NotSupportedException($"{s.Kind} surface"),
        };
    }

    /// <summary>The surface point at (u, v).</summary>
    public static Point3D At(Surface s, double u, double v)
    {
        var (x, y, z) = Axes(s);
        var radial = x * Math.Cos(u) + y * Math.Sin(u);
        return s.Kind switch
        {
            SurfaceKind.Plane => s.Origin + x * u + y * v,
            SurfaceKind.Cylinder => s.Origin + radial * s.Radius + z * v,
            SurfaceKind.Cone => s.Origin + radial * (v * Math.Tan(s.Angle)) + z * v,
            SurfaceKind.Sphere => s.Origin + radial * (s.Radius * Math.Cos(v)) + z * (s.Radius * Math.Sin(v)),
            SurfaceKind.Torus => s.Origin + radial * (s.Radius + s.Minor * Math.Cos(v)) + z * (s.Minor * Math.Sin(v)),
            _ => throw new NotSupportedException($"{s.Kind} surface"),
        };
    }

    public static bool PeriodicU(Surface s) => s.Kind != SurfaceKind.Plane;

    public static bool PeriodicV(Surface s) => s.Kind == SurfaceKind.Torus;

    /// <summary>
    /// The image of <paramref name="curve"/> over [t0, t1] in the surface's parameters, continuous (angles are
    /// unwrapped, so a closed edge round the axis ends 2π from where it starts). Straight images are exact; others
    /// are degree-9 pieces interpolating the curve at Chebyshev points, split until they are within 1e-11.
    /// </summary>
    public static UvCurve Image(Surface s, Curve curve, double t0, double t1)
    {
        // Straight when a handful of points lie on the line between the ends (as exact as the angles themselves).
        var probe = Enumerable.Range(1, 7).Select(i => t0 + (t1 - t0) * i / 8).ToArray();
        var track = Track(s, curve, [t0, .. probe, t1]);
        if (Enumerable.Range(1, probe.Length).All(i =>
            {
                var f = (probe[i - 1] - t0) / (t1 - t0);
                return Math.Abs(track[i].U - (track[0].U + (track[^1].U - track[0].U) * f)) <= 1e-9
                    && Math.Abs(track[i].V - (track[0].V + (track[^1].V - track[0].V) * f)) <= 1e-9;
            }))
            return new UvCurve(1, [track[0], track[^1]], [t0, t1], [2, 2]);

        for (var spans = Math.Max(1, (int)Math.Ceiling((t1 - t0) / (Math.PI / 8))); ; spans *= 2)
        {
            var (poles, error) = Pieces(s, curve, t0, t1, spans);
            if (error <= 1e-11 || spans >= 512)
            {
                var knots = Enumerable.Range(0, spans + 1).Select(k => k == spans ? t1 : t0 + (t1 - t0) * k / spans).ToList();
                var mults = Enumerable.Range(0, spans + 1).Select(k => k == 0 || k == spans ? Degree + 1 : Degree).ToList();
                return new UvCurve(Degree, poles, knots, mults);
            }
        }
    }

    /// <summary>
    /// Bézier pieces of the image, joined end to end, and the largest error halfway between their nodes. The nodes of
    /// all pieces are tracked together, so every piece lies on the same unwrapped branch.
    /// </summary>
    static (List<(double U, double V)> Poles, double Error) Pieces(Surface s, Curve curve, double t0, double t1, int spans)
    {
        var h = (t1 - t0) / spans;
        var nodes = Enumerable.Range(0, Degree + 1).Select(i => (1 - Math.Cos(Math.PI * i / Degree)) / 2).ToArray();
        var checks = Enumerable.Range(0, Degree).Select(i => (nodes[i] + nodes[i + 1]) / 2).ToArray();
        // Every node and check point in order along the curve, so the unwrapping follows it closely.
        var local = nodes.Concat(checks).Order().ToArray();
        var ts = new List<double>();
        for (var k = 0; k < spans; k++)
            foreach (var x in local)
                if (k == 0 || x > 0) ts.Add(k == spans - 1 && x == 1 ? t1 : t0 + h * (k + x));
        var track = Track(s, curve, ts);

        var basis = new double[Degree + 1, Degree + 1];
        for (var i = 0; i <= Degree; i++)
            for (var j = 0; j <= Degree; j++) basis[i, j] = Bernstein(j, nodes[i]);
        var poles = new List<(double U, double V)>();
        var error = 0.0;
        var perSpan = local.Length - 1;
        for (var k = 0; k < spans; k++)
        {
            var at = local.Select((x, i) => (x, track[k * perSpan + i])).ToDictionary(e => e.x, e => e.Item2);
            var (cu, cv) = (Solve(basis, nodes.Select(x => at[x].U).ToArray()), Solve(basis, nodes.Select(x => at[x].V).ToArray()));
            for (var j = k == 0 ? 0 : 1; j <= Degree; j++) poles.Add((cu[j], cv[j]));
            foreach (var x in checks)
            {
                var (u, v) = (0.0, 0.0);
                for (var j = 0; j <= Degree; j++) (u, v) = (u + cu[j] * Bernstein(j, x), v + cv[j] * Bernstein(j, x));
                error = Math.Max(error, Math.Max(Math.Abs(u - at[x].U), Math.Abs(v - at[x].V)));
            }
        }
        return (poles, error);
    }

    /// <summary>(u, v) at each parameter, angles unwrapped along the way; on the axis u takes its neighbour's value.</summary>
    static List<(double U, double V)> Track(Surface s, Curve curve, IReadOnlyList<double> ts)
    {
        var raw = ts.Select(t => Of(s, curve.At(t))).ToList();
        var firstU = raw.FirstOrDefault(p => !double.IsNaN(p.U)).U;
        var result = new List<(double U, double V)>(raw.Count);
        foreach (var (u0, v0) in raw)
        {
            var (u, v) = (double.IsNaN(u0) ? (result.Count > 0 ? result[^1].U : firstU) : u0, v0);
            if (result.Count > 0)
            {
                if (PeriodicU(s)) u += Turns(result[^1].U - u);
                if (PeriodicV(s)) v += Turns(result[^1].V - v);
            }
            result.Add((u, v));
        }
        return result;
    }

    /// <summary>The multiple of 2π nearest to <paramref name="gap"/>.</summary>
    public static double Turns(double gap) => 2 * Math.PI * Math.Round(gap / (2 * Math.PI));

    static double Bernstein(int j, double x)
    {
        var binomial = 1.0;
        for (var i = 1; i <= j; i++) binomial = binomial * (Degree - j + i) / i;
        return binomial * Math.Pow(x, j) * Math.Pow(1 - x, Degree - j);
    }

    /// <summary>Gaussian elimination with partial pivoting; the matrix is small and well conditioned.</summary>
    static double[] Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (var c = 0; c < n; c++)
        {
            var p = Enumerable.Range(c, n - c).MaxBy(r => Math.Abs(m[r, c]));
            for (var k = 0; k < n; k++) (m[c, k], m[p, k]) = (m[p, k], m[c, k]);
            (x[c], x[p]) = (x[p], x[c]);
            for (var r = c + 1; r < n; r++)
            {
                var f = m[r, c] / m[c, c];
                for (var k = c; k < n; k++) m[r, k] -= f * m[c, k];
                x[r] -= f * x[c];
            }
        }
        for (var r = n - 1; r >= 0; r--)
        {
            for (var k = r + 1; k < n; k++) x[r] -= m[r, k] * x[k];
            x[r] /= m[r, r];
        }
        return x;
    }
}
