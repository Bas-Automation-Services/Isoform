using BasAutomation.Geometry;

namespace Isoform.Brep;

/// <summary>
/// Volume and area computed from the B-rep itself, without tessellating. A surface integral over a face becomes a
/// boundary integral over its loops in (u, v) by Green's theorem: <c>∬ f du dv = ∮ Q dv</c> with
/// <c>Q(u, v) = ∫ f(s, v) ds</c> from a fixed u*. Both integrals are Gauss–Legendre, so the result is exact for the
/// polynomial integrands of planes and to rounding for the trigonometric ones of cylinders.
/// </summary>
public static class MassProperties
{
    /// <summary>The enclosed volume: the outer shell's minus the voids'.</summary>
    public static double Volume(Body body) => body.Shells.Select((_, s) => Volume(body, new ShellId(s))).Sum();

    /// <summary>
    /// The volume one shell encloses, positive when its faces point outwards (divergence theorem,
    /// <c>V = ⅓ ∬ (x − p) · n dA</c>, with p one of its vertices to keep the numbers small).
    /// </summary>
    public static double Volume(Body body, ShellId shell)
    {
        var faces = body[shell].Faces;
        if (faces.IsEmpty) return 0;
        var p = body[body.StartOf(body[body[faces[0]].Loops[0]].First)].Point;
        double total = 0;
        foreach (var f in faces)
        {
            var s = body[f].Surface;
            // (S − p) · (Su × Sv) du dv is (x − p) · n dA with n along Su × Sv. Green's theorem over the loops as they
            // run gives the integral with the face's own orientation, so the sense flag drops out.
            total += Boundary(body, f, (u, v) => (s.At(u, v) - p).Dot(s.DerivativeU(u, v).Cross(s.DerivativeV(u, v))));
        }
        return total / 3;
    }

    /// <summary>A face's area.</summary>
    public static double Area(Body body, FaceId face)
    {
        var s = body[face].Surface;
        var sense = body[face].SameSense ? 1 : -1;
        return sense * Boundary(body, face, (u, v) => s.DerivativeU(u, v).Cross(s.DerivativeV(u, v)).Length);
    }

    /// <summary>
    /// ∮ u dv round one loop: its signed area in (u, v), positive when it runs counter-clockwise there (round the
    /// surface normal).
    /// </summary>
    public static double SignedUvArea(Body body, LoopId loop) =>
        body.CoedgesOf(loop).Sum(c =>
        {
            var pc = body[c].Pcurve;
            var (from, to) = body.ParametersOf(c);
            return Quadrature.Integrate(t => pc.At(t).X * pc.Derivative(t).Y, from, to, body[body[c].Edge].Curve.Period is null ? null : Quadrature.AngleStep);
        });

    // ∮ Q(u, v) dv over every loop of the face, Q(u, v) = ∫ f(s, v) ds from u* (the face's first p-curve start).
    // This is ∬ f du dv over the face where its loops run counter-clockwise in (u, v), and minus it where they run
    // clockwise (a face against its surface's sense).
    static double Boundary(Body body, FaceId face, Func<double, double, double> f)
    {
        var f0 = body[face];
        var first = body[body[f0.Loops[0]].First];
        var uStar = first.Pcurve.At(body.ParametersOf(body[f0.Loops[0]].First).From).X;
        double? uStep = f0.Surface.PeriodU is null ? null : Quadrature.AngleStep;
        double total = 0;
        foreach (var loop in f0.Loops)
        foreach (var c in body.CoedgesOf(loop))
        {
            var pc = body[c].Pcurve;
            var (from, to) = body.ParametersOf(c);
            double? tStep = body[body[c].Edge].Curve.Period is null ? null : Quadrature.AngleStep;
            total += Quadrature.Integrate(t =>
            {
                var dv = pc.Derivative(t).Y;
                if (dv == 0) return 0;
                var uv = pc.At(t);
                return Quadrature.Integrate(s => f(s, uv.Y), uStar, uv.X, uStep) * dv;
            }, from, to, tStep);
        }
        return total;
    }
}

/// <summary>Gauss–Legendre quadrature.</summary>
static class Quadrature
{
    /// <summary>The longest piece an angular parameter is integrated over at once.</summary>
    public const double AngleStep = Math.PI / 4;

    // 24 points: exact for polynomials up to degree 47; for trigonometric integrands over π/4 the error is far
    // below rounding.
    static readonly (double[] X, double[] W) Rule = Legendre(24);

    /// <summary>
    /// ∫ f from a to b (signed: negative when b &lt; a), in pieces no longer than <paramref name="step"/> (one piece
    /// when null).
    /// </summary>
    public static double Integrate(Func<double, double> f, double a, double b, double? step = null)
    {
        if (a == b) return 0;
        var pieces = step is { } h ? Math.Max(1, (int)Math.Ceiling(Math.Abs(b - a) / h)) : 1;
        var width = (b - a) / pieces;
        double total = 0;
        for (var p = 0; p < pieces; p++)
        {
            var mid = a + (p + 0.5) * width;
            double sum = 0;
            for (var i = 0; i < Rule.X.Length; i++)
                sum += Rule.W[i] * f(mid + 0.5 * width * Rule.X[i]);
            total += 0.5 * width * sum;
        }
        return total;
    }

    // Nodes as roots of P_n by Newton from Chebyshev guesses; weights 2 / ((1 − x²) P_n'(x)²).
    static (double[], double[]) Legendre(int n)
    {
        var x = new double[n];
        var w = new double[n];
        for (var i = 0; i < n; i++)
        {
            var z = Math.Cos(Math.PI * (i + 0.75) / (n + 0.5));
            for (var iteration = 0; iteration < 100; iteration++)
            {
                var (p, dp) = Evaluate(n, z);
                var step = p / dp;
                z -= step;
                if (Math.Abs(step) <= 1e-15) break;
            }
            x[i] = z;
            var slope = Evaluate(n, z).Derivative;
            w[i] = 2 / ((1 - z * z) * slope * slope);
        }
        return (x, w);
    }

    // P_n(z) and P_n'(z) by the three-term recurrence.
    static (double Value, double Derivative) Evaluate(int n, double z)
    {
        double p0 = 1, p1 = z;
        for (var k = 2; k <= n; k++) (p0, p1) = (p1, ((2 * k - 1) * z * p1 - (k - 1) * p0) / k);
        return (p1, n * (z * p1 - p0) / (z * z - 1));
    }
}
