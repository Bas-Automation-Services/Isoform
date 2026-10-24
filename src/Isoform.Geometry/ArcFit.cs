using BasAutomation.Geometry;

namespace Isoform.Geometry;

/// <summary>
/// A line (no <see cref="Center"/>) or a circular arc of a 2D outline, from <see cref="Start"/> to <see cref="End"/>.
/// An arc turns counter-clockwise round its centre when <see cref="Ccw"/>; one that ends where it starts is a full circle.
/// </summary>
public readonly record struct Segment2(Point2D Start, Point2D End, Point2D? Center = null, bool Ccw = true)
{
    public bool IsArc => Center.HasValue;

    public double Radius => Center is { } c ? (Start - c).Length : double.PositiveInfinity;

    /// <summary>The signed turn round the centre, positive counter-clockwise, ±2π for a full circle; 0 for a line.</summary>
    public double Sweep
    {
        get
        {
            if (Center is not { } c) return 0;
            var d = Angle(End - c) - Angle(Start - c);
            if (Ccw) { while (d <= 0) d += 2 * Math.PI; }
            else { while (d >= 0) d -= 2 * Math.PI; }
            return d;
        }
    }

    public double Length => IsArc ? Radius * Math.Abs(Sweep) : (End - Start).Length;

    /// <summary>The point a fraction <paramref name="s"/> (0 to 1) of the way along.</summary>
    public Point2D At(double s)
    {
        if (Center is not { } c) return Start + (End - Start) * s;
        var a = Angle(Start - c) + Sweep * s;
        return c + new Vector2D(Math.Cos(a), Math.Sin(a)) * Radius;
    }

    /// <summary>The unit tangent a fraction <paramref name="s"/> of the way along, in the direction of travel.</summary>
    public Vector2D Tangent(double s)
    {
        if (Center is not { } c) return (End - Start).Normalized();
        var r = (At(s) - c).Normalized();
        return Ccw ? new Vector2D(-r.Y, r.X) : new Vector2D(r.Y, -r.X);
    }

    /// <summary>The distance from <paramref name="p"/> to the segment, its ends included.</summary>
    public double Distance(Point2D p)
    {
        if (Center is not { } c)
        {
            var d = End - Start;
            var t = d.LengthSquared > 0 ? Math.Clamp((p - Start).Dot(d) / d.LengthSquared, 0, 1) : 0;
            return (p - (Start + d * t)).Length;
        }
        var sweep = Sweep;
        var phi = Angle(p - c) - Angle(Start - c);
        if (sweep > 0) { while (phi < 0) phi += 2 * Math.PI; }
        else { while (phi > 0) phi -= 2 * Math.PI; }
        return Math.Abs(phi) <= Math.Abs(sweep) ? Math.Abs((p - c).Length - Radius) : Math.Min((p - Start).Length, (p - End).Length);
    }

    /// <summary>
    /// The arc that leaves <paramref name="start"/> along the unit tangent <paramref name="t"/> and ends at
    /// <paramref name="end"/>; a line where the chord runs along the tangent.
    /// </summary>
    public static Segment2 Leaving(Point2D start, Vector2D t, Point2D end)
    {
        var chord = end - start;
        var turn = t.Cross(chord);
        if (Math.Abs(turn) <= 1e-12 * chord.LengthSquared) return new(start, end);
        // The centre is on the left normal at the signed distance ρ that puts both ends on the circle.
        var rho = chord.LengthSquared / (2 * turn);
        return new(start, end, start + new Vector2D(-t.Y, t.X) * rho, rho > 0);
    }

    static double Angle(Vector2D v) => Math.Atan2(v.Y, v.X);
}

/// <summary>Arcs fitted to a curve, and the largest distance between the two.</summary>
public sealed record ArcFitResult(IReadOnlyList<Segment2> Segments, double Error);

/// <summary>
/// Tangent arcs through a smooth 2D curve, given as ordered points with their unit tangents. The chain starts at the
/// first point along its tangent and ends at the last along its tangent, so it runs on smoothly into whatever the curve
/// runs into; inside, each joint is smooth too. A span of the curve is fitted with a biarc (two arcs meeting
/// tangentially; equal tangent legs, or failing that a few other ratios) and split at its middle point while the biarc strays more than the tolerance
/// from the curve, either way: no curve point farther than that from the arcs and no arc point farther from the
/// curve between its points. The points must be dense enough that the curve between them is an arc to well within the
/// tolerance.
/// </summary>
public static class ArcFit
{
    const int Samples = 16;

    // Leg ratios tried, the first leg's to the second's: 1 is the equal-legged biarc.
    static readonly double[] Ratios = [1, 0.6, 1.6, 0.35, 2.8];

    public static ArcFitResult Fit(IReadOnlyList<Point2D> points, IReadOnlyList<Vector2D> tangents, double tolerance)
    {
        var along = new double[points.Count];
        for (var k = 1; k < points.Count; k++) along[k] = along[k - 1] + (points[k] - points[k - 1]).Length;
        var segments = new List<Segment2>();
        var error = 0.0;

        void Span(int i, int j)
        {
            // A closed span has no chord to fit: split it first.
            var closed = (points[j] - points[i]).Length <= 1e-9 * (1 + along[j] - along[i]);
            // The equal-legged biarc first, then joints nearer either end, until one is within the tolerance.
            (Segment2 A, Segment2 B)? best = null;
            var least = double.MaxValue;
            foreach (var ratio in Ratios)
            {
                if (closed || Biarc(points[i], tangents[i], points[j], tangents[j], ratio) is not { } pair) continue;
                var e = Error(points, tangents, i, j, pair);
                if (e < least) (best, least) = (pair, e);
                if (e <= tolerance) break;
            }
            if (best is { } fit && (least <= tolerance || j - i < 2))
            {
                segments.Add(fit.A);
                segments.Add(fit.B);
                error = Math.Max(error, least);
                return;
            }
            if (j - i < 2)
            {
                segments.Add(new Segment2(points[i], points[j]));
                error = Math.Max(error, Error(points, tangents, i, j, (segments[^1], segments[^1])));
                return;
            }
            var half = (along[i] + along[j]) / 2;
            var b = Array.BinarySearch(along, i, j - i + 1, half);
            var m = Math.Clamp(b >= 0 ? b : ~b, i + 1, j - 1);
            Span(i, m);
            Span(m, j);
        }

        Span(0, points.Count - 1);
        return new ArcFitResult(Merge(segments), error);
    }

    /// <summary>
    /// The biarc from p0 along t0 to p1 arriving along t1 with tangent legs d₁ = <paramref name="ratio"/> · d₂ (1: equal
    /// legs): the joint divides the segment from p0 + d₁·t0 to p1 − d₂·t1 in that ratio. Null where no positive legs exist.
    /// </summary>
    public static (Segment2 A, Segment2 B)? Biarc(Point2D p0, Vector2D t0, Point2D p1, Vector2D t1, double ratio = 1)
    {
        // The legs meet the joint when |q1 − q0| = d₁ + d₂: v·v − 2d₁ v·t0 − 2d₂ v·t1 − 2d₁d₂(1 − t0·t1) = 0.
        var v = p1 - p0;
        var (vv, v0, v1, a) = (v.LengthSquared, v.Dot(t0), v.Dot(t1), 1 - t0.Dot(t1));
        var (qa, qb) = (2 * ratio * a, 2 * (ratio * v0 + v1));
        var d2 = qa <= 1e-12 ? vv / qb : (-qb + Math.Sqrt(qb * qb + 4 * qa * vv)) / (2 * qa);
        var d1 = ratio * d2;
        if (!(d1 > 0 && d2 > 0) || double.IsInfinity(d2)) return null;
        var (q0, q1) = (p0 + t0 * d1, p1 - t1 * d2);
        var joint = q0 + (q1 - q0) * (d1 / (d1 + d2));
        var first = Segment2.Leaving(p0, t0, joint);
        var back = Segment2.Leaving(p1, -t1, joint);
        return (first, back with { Start = joint, End = p1, Ccw = !back.Ccw });
    }

    // Both ways: every curve point to the nearer arc, and points along each arc to the curve, taken between each two of
    // its points as the arc leaving the first along its tangent (true to third order, where a chord is to second).
    static double Error(IReadOnlyList<Point2D> points, IReadOnlyList<Vector2D> tangents, int i, int j, (Segment2 A, Segment2 B) pair)
    {
        var e = 0.0;
        for (var k = i + 1; k < j; k++) e = Math.Max(e, Math.Min(pair.A.Distance(points[k]), pair.B.Distance(points[k])));
        foreach (var arc in new[] { pair.A, pair.B })
            for (var s = 1; s < Samples; s++)
            {
                var q = arc.At((double)s / Samples);
                var near = double.MaxValue;
                for (var k = i; k < j; k++) near = Math.Min(near, Segment2.Leaving(points[k], tangents[k], points[k + 1]).Distance(q));
                e = Math.Max(e, near);
            }
        return e;
    }

    // Neighbours on one circle (a biarc of a circular span) or one line become one segment.
    static List<Segment2> Merge(List<Segment2> segments)
    {
        var merged = new List<Segment2>();
        foreach (var s in segments)
        {
            if (merged.Count > 0 && Same(merged[^1], s)) merged[^1] = merged[^1] with { End = s.End };
            else merged.Add(s);
        }
        return merged;

        static bool Same(Segment2 a, Segment2 b)
        {
            if (a.IsArc != b.IsArc) return false;
            if (!a.IsArc) return Math.Abs((a.End - a.Start).Normalized().Cross((b.End - b.Start).Normalized())) <= 1e-12;
            var tol = 1e-9 * (1 + a.Radius);
            return a.Ccw == b.Ccw && (a.Center!.Value - b.Center!.Value).Length <= tol && Math.Abs(a.Radius - b.Radius) <= tol
                && Math.Abs(a.Sweep) + Math.Abs(b.Sweep) < 2 * Math.PI - 1e-9;
        }
    }
}
