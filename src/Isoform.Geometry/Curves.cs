using BasAutomation.Geometry;

namespace Isoform.Geometry;

/// <summary>An exact edge curve, parametrised by <c>t</c>.</summary>
public abstract record Curve
{
    public abstract Point3D At(double t);

    public abstract Vector3D Tangent(double t);

    /// <summary>The parameter of the curve point nearest to <paramref name="p"/>.</summary>
    public abstract double Parameter(Point3D p);
}

/// <summary>Origin + t · Direction, with a unit direction.</summary>
public sealed record Line(Point3D Origin, Vector3D Direction) : Curve
{
    public override Point3D At(double t) => Origin + Direction * t;
    public override Vector3D Tangent(double t) => Direction;
    public override double Parameter(Point3D p) => Direction.Dot(p - Origin);
}

/// <summary>A circle counter-clockwise round <see cref="Normal"/>, starting at <see cref="X"/>; t is the angle.</summary>
public sealed record Circle(Point3D Center, Vector3D Normal, Vector3D X, double Radius) : Curve
{
    public Vector3D Y => Normal.Cross(X);
    public override Point3D At(double t) => Center + (X * Math.Cos(t) + Y * Math.Sin(t)) * Radius;
    public override Vector3D Tangent(double t) => (Y * Math.Cos(t) - X * Math.Sin(t)) * Radius;
    public override double Parameter(Point3D p) => Math.Atan2(Y.Dot(p - Center), X.Dot(p - Center));
}

/// <summary>One branch of a hyperbola, <c>Center + Major cosh t X + Minor sinh t Y</c>, Y = Normal × X.</summary>
public sealed record Hyperbola(Point3D Center, Vector3D Normal, Vector3D X, double Major, double Minor) : Curve
{
    public Vector3D Y => Normal.Cross(X);
    public override Point3D At(double t) => Center + X * (Major * Math.Cosh(t)) + Y * (Minor * Math.Sinh(t));
    public override Vector3D Tangent(double t) => X * (Major * Math.Sinh(t)) + Y * (Minor * Math.Cosh(t));
    public override double Parameter(Point3D p) => Math.Asinh(Y.Dot(p - Center) / Minor);
}

/// <summary>An ellipse counter-clockwise round <see cref="Normal"/>, its major axis along <see cref="X"/>.</summary>
public sealed record Ellipse(Point3D Center, Vector3D Normal, Vector3D X, double Major, double Minor) : Curve
{
    public Vector3D Y => Normal.Cross(X);
    public override Point3D At(double t) => Center + X * (Major * Math.Cos(t)) + Y * (Minor * Math.Sin(t));
    public override Vector3D Tangent(double t) => Y * (Minor * Math.Cos(t)) - X * (Major * Math.Sin(t));
    public override double Parameter(Point3D p) => Math.Atan2(Y.Dot(p - Center) / Minor, X.Dot(p - Center) / Major);
}

/// <summary>
/// A B-spline in Bézier form: the span from <c>Knots[k]</c> to <c>Knots[k + 1]</c> has the control points
/// <c>Poles[Degree · k]</c> to <c>Poles[Degree · (k + 1)]</c> (interior knots of multiplicity Degree). A traced curve,
/// fitted piece by piece.
/// </summary>
public sealed record BSpline(int Degree, IReadOnlyList<Point3D> Poles, IReadOnlyList<double> Knots) : Curve
{
    (int K, double S) Span(double t)
    {
        var k = 0;
        var (lo, hi) = (0, Knots.Count - 2);
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Knots[mid] <= t) (k, lo) = (mid, mid + 1); else hi = mid - 1;
        }
        return (k, (t - Knots[k]) / (Knots[k + 1] - Knots[k]));
    }

    // de Casteljau on the span's control points (less the first, as vectors from it) down to `order` + 1 of them.
    Vector3D[] Reduced(int k, double s, int order)
    {
        var p0 = Poles[Degree * k];
        var v = Enumerable.Range(0, Degree + 1).Select(i => Poles[Degree * k + i] - p0).ToArray();
        for (var n = Degree; n > order; n--)
            for (var i = 0; i < n; i++) v[i] = v[i] + (v[i + 1] - v[i]) * s;
        return v;
    }

    public override Point3D At(double t)
    {
        var (k, s) = Span(t);
        return Poles[Degree * k] + Reduced(k, s, 0)[0];
    }

    public override Vector3D Tangent(double t)
    {
        var (k, s) = Span(t);
        var v = Reduced(k, s, 1);
        return (v[1] - v[0]) * (Degree / (Knots[k + 1] - Knots[k]));
    }

    /// <summary>The nearest of a few points per span, then a golden-section search round it.</summary>
    public override double Parameter(Point3D p)
    {
        double D(double t) => (At(t) - p).LengthSquared;
        var ts = Knots.Take(Knots.Count - 1).SelectMany((k, i) => Enumerable.Range(0, 8).Select(j => k + (Knots[i + 1] - k) * j / 8)).Append(Knots[^1]).ToList();
        var best = Enumerable.Range(0, ts.Count).MinBy(i => D(ts[i]));
        var (a, b) = (ts[Math.Max(0, best - 1)], ts[Math.Min(ts.Count - 1, best + 1)]);
        const double g = 0.6180339887498949;
        for (var i = 0; i < 100; i++)
        {
            var (x1, x2) = (b - g * (b - a), a + g * (b - a));
            if (D(x1) < D(x2)) b = x2; else a = x1;
        }
        return (a + b) / 2;
    }
}
