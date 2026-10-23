using Isoform.Document;

namespace Isoform.Sketching;

/// <summary>
/// Where an entity meets another: the point, how far along the entity it lies (a line: 0 at its From to 1 at its To;
/// an arc: radians counter-clockwise from its From; a circle: radians counter-clockwise from +x), and the other entity.
/// </summary>
public readonly record struct SketchHit(double X, double Y, double At, string Other);

/// <summary>
/// Closed-form intersections of a sketch's lines, arcs and circles at their stored coordinates: line and line, line
/// and circle, circle and circle, lines kept to their segments and arcs to their sweeps, ends included. A tangent
/// contact is one hit. Overlapping lines and concentric circles meet in no single point and give none.
/// </summary>
public static class SketchIntersections
{
    /// <summary>Points closer than this (mm) are one; a line this near a circle touches it.</summary>
    public const double Touch = 1e-7;

    /// <summary>
    /// Where <paramref name="entity"/> meets the sketch's other entities that are not construction geometry, in order
    /// along it (then by the other's id).
    /// </summary>
    public static IReadOnlyList<SketchHit> Of(Sketch sketch, string entity)
    {
        var e = Curve.Of(sketch, sketch.Entities.First(k => k.Id == entity));
        return [.. sketch.Entities.Where(o => o.Id != entity && !o.Construction)
            .SelectMany(o => Between(e, Curve.Of(sketch, o)).Select(p => new SketchHit(p.X, p.Y, e.At(p), o.Id)))
            .OrderBy(h => h.At).ThenBy(h => h.Other, StringComparer.Ordinal)];
    }

    static IEnumerable<(double X, double Y)> Between(Curve a, Curve b) => ((a.Round, b.Round) switch
    {
        (false, false) => Lines(a.P, a.Q, b.P, b.Q),
        (false, true) => LineCircle(a.P, a.Q, b.C, b.R),
        (true, false) => LineCircle(b.P, b.Q, a.C, a.R),
        _ => Circles(a.C, a.R, b.C, b.R),
    }).Where(p => a.Holds(p) && b.Holds(p));

    // The two whole lines: none when parallel.
    static IEnumerable<(double X, double Y)> Lines((double X, double Y) p, (double X, double Y) q, (double X, double Y) r, (double X, double Y) s)
    {
        var (d, e) = ((q.X - p.X, q.Y - p.Y), (s.X - r.X, s.Y - r.Y));
        var cross = d.Item1 * e.Item2 - d.Item2 * e.Item1;
        if (Math.Abs(cross) <= 1e-12 * Math.Sqrt((d.Item1 * d.Item1 + d.Item2 * d.Item2) * (e.Item1 * e.Item1 + e.Item2 * e.Item2))) yield break;
        var t = ((r.X - p.X) * e.Item2 - (r.Y - p.Y) * e.Item1) / cross;
        yield return (p.X + t * d.Item1, p.Y + t * d.Item2);
    }

    // The whole line and the circle, in order along the line: the foot of the centre when it touches.
    static IEnumerable<(double X, double Y)> LineCircle((double X, double Y) p, (double X, double Y) q, (double X, double Y) c, double r)
    {
        var length = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
        var (ux, uy) = ((q.X - p.X) / length, (q.Y - p.Y) / length);
        var along = (c.X - p.X) * ux + (c.Y - p.Y) * uy;
        var (fx, fy) = (p.X + along * ux, p.Y + along * uy);
        var h = Math.Sqrt((c.X - fx) * (c.X - fx) + (c.Y - fy) * (c.Y - fy));
        if (h > r + Touch) yield break;
        if (h >= r - Touch)
        {
            yield return (fx, fy);
            yield break;
        }
        var s = Math.Sqrt(r * r - h * h);
        yield return (fx - s * ux, fy - s * uy);
        yield return (fx + s * ux, fy + s * uy);
    }

    // Two whole circles: one point where they touch, none when concentric.
    static IEnumerable<(double X, double Y)> Circles((double X, double Y) c1, double r1, (double X, double Y) c2, double r2)
    {
        var d = Math.Sqrt((c2.X - c1.X) * (c2.X - c1.X) + (c2.Y - c1.Y) * (c2.Y - c1.Y));
        if (d <= Touch || d > r1 + r2 + Touch || d < Math.Abs(r1 - r2) - Touch) yield break;
        var (ux, uy) = ((c2.X - c1.X) / d, (c2.Y - c1.Y) / d);
        var a = (d * d + r1 * r1 - r2 * r2) / (2 * d);
        if (d >= r1 + r2 - Touch || d <= Math.Abs(r1 - r2) + Touch)
        {
            // Touching: the contact on the line through both centres, on c1's side the sign of a gives.
            var at = Math.Sign(a) * r1;
            yield return (c1.X + at * ux, c1.Y + at * uy);
            yield break;
        }
        var h = Math.Sqrt(r1 * r1 - a * a);
        yield return (c1.X + a * ux + h * uy, c1.Y + a * uy - h * ux);
        yield return (c1.X + a * ux - h * uy, c1.Y + a * uy + h * ux);
    }
}

/// <summary>
/// A line from <see cref="P"/> to <see cref="Q"/>, or a circle round <see cref="C"/> (an arc: from angle
/// <see cref="A0"/> counter-clockwise through <see cref="Sweep"/>), at a sketch's stored coordinates.
/// </summary>
readonly record struct Curve(bool Round, (double X, double Y) P, (double X, double Y) Q, (double X, double Y) C, double R, double A0, double Sweep)
{
    public static Curve Of(Sketch s, SketchEntity e)
    {
        (double X, double Y) At(string p) => (s.Points[p][0], s.Points[p][1]);
        switch (e)
        {
            case SketchLine l:
                return new(false, At(l.From), At(l.To), default, 0, 0, 0);
            case SketchArc a:
            {
                var (c, f, t) = (At(a.Center), At(a.From), At(a.To));
                var a0 = Math.Atan2(f.Y - c.Y, f.X - c.X);
                var sweep = Math.Atan2(t.Y - c.Y, t.X - c.X) - a0;
                if (sweep <= 0) sweep += 2 * Math.PI;
                return new(true, default, default, c, Math.Sqrt((f.X - c.X) * (f.X - c.X) + (f.Y - c.Y) * (f.Y - c.Y)), a0, sweep);
            }
            default:
            {
                var c = (SketchCircle)e;
                return new(true, default, default, At(c.Center), c.Radius, 0, 2 * Math.PI);
            }
        }
    }

    double Length => Math.Sqrt((Q.X - P.X) * (Q.X - P.X) + (Q.Y - P.Y) * (Q.Y - P.Y));

    /// <summary>Millimetres per unit of <see cref="At"/>: the line's length, or the radius.</summary>
    public double Scale => Round ? R : Length;

    /// <summary>Where <see cref="At"/> ends: 1 on a line, the sweep on an arc or circle.</summary>
    public double End => Round ? Sweep : 1;

    /// <summary>How far along a point lies: a line's 0 to 1, or the angle from the start; just before the start counts as 0.</summary>
    public double At((double X, double Y) p)
    {
        if (!Round) return ((p.X - P.X) * (Q.X - P.X) + (p.Y - P.Y) * (Q.Y - P.Y)) / (Length * Length);
        var a = Angle(p, A0);
        return a > 2 * Math.PI - SketchIntersections.Touch / R ? 0 : a;
    }

    public (double X, double Y) Point(double at) => Round
        ? (C.X + R * Math.Cos(A0 + at), C.Y + R * Math.Sin(A0 + at))
        : (P.X + at * (Q.X - P.X), P.Y + at * (Q.Y - P.Y));

    /// <summary>A point on the curve lies within its segment or sweep, ends included.</summary>
    public bool Holds((double X, double Y) p) => At(p) is var at && at * Scale >= -SketchIntersections.Touch && (at - End) * Scale <= SketchIntersections.Touch;

    /// <summary>The distance from a point to the piece between <paramref name="from"/> and <paramref name="to"/>.</summary>
    public double Distance((double X, double Y) p, double from, double to)
    {
        var (a, b) = (Point(from), Point(to));
        double Far((double X, double Y) q) => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
        if (Round)
            return Angle(p, A0 + from) <= to - from ? Math.Abs(Far(C) - R) : Math.Min(Far(a), Far(b));
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        return Far((a.X + t * dx, a.Y + t * dy));
    }

    // The angle of p round the centre, counter-clockwise from `start`, in [0, 2π).
    double Angle((double X, double Y) p, double start)
    {
        var a = (Math.Atan2(p.Y - C.Y, p.X - C.X) - start) % (2 * Math.PI);
        return a < 0 ? a + 2 * Math.PI : a;
    }
}
