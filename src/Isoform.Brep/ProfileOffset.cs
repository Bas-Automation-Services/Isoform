using BasAutomation.Geometry;

namespace Isoform.Brep;

/// <summary>
/// A profile loop grown sideways, as an offset moves the faces it sweeps: each segment moved right of travel by its own
/// distance (a line along its normal, an arc's radius changed), neighbours extended or trimmed to meet, and where they
/// part (a corner turning left moved right, or turning right moved left) a round corner of that radius round the old
/// vertex. Corners between segments moved by different distances, or that a caller keeps sharp, are always met.
/// </summary>
public static class ProfileOffset
{
    const double Turn = 1e-12;

    /// <summary>
    /// The grown loop. <c>Origins</c> gives for each of its segments the segment it came from, or for a round corner
    /// <c>~k</c>: the corner at the end of segment k. A problem when a segment is used up or an arc shrinks to nothing.
    /// </summary>
    public static (ProfileLoop? Loop, int[] Origins, string? Problem) Grow(ProfileLoop loop, IReadOnlyList<double> by, Func<Point2D, bool> sharp, double tolerance)
    {
        var segments = loop.Segments;
        var n = segments.Count;
        var radius = new double[n];
        for (var i = 0; i < n; i++)
        {
            if (segments[i] is not ArcSegment arc) continue;
            radius[i] = arc.Radius + by[i] * Math.Sign(arc.Sweep);
            if (radius[i] <= tolerance) return (null, [], $"segment {i}, an arc, shrinks to nothing");
        }
        if (n == 1 && segments[0] is ArcSegment { IsFullCircle: true } circle)
            return (new ProfileLoop([circle with { Radius = radius[0] }]), [0], null);

        // Where each moved segment starts and ends, and the round corner after it.
        var (start, end) = (new Point2D[n], new Point2D[n]);
        var corner = new ArcSegment?[n];
        for (var k = 0; k < n; k++)
        {
            var j = (k + 1) % n;
            var v = segments[k].End;
            var (a, b) = (Tangent(segments[k], atEnd: true), Tangent(segments[j], atEnd: false));
            var cross = a.X * b.Y - a.Y * b.X;
            var (from, to) = (v + Right(a) * by[k], v + Right(b) * by[j]);
            if (Math.Abs(cross) <= Turn && a.Dot(b) > 0)
            {
                if (Math.Abs(by[k] - by[j]) > Turn) return (null, [], $"segments {k} and {j} run on smoothly but move apart");
                (end[k], start[j]) = (from, to);
            }
            else if (Math.Abs(by[k] - by[j]) <= Turn && Math.Abs(by[k]) > Turn && Math.Sign(cross) == Math.Sign(by[k]) && !sharp(v))
            {
                (end[k], start[j]) = (from, to);
                var (p, q) = (from - v, to - v);
                corner[k] = new ArcSegment(v, Math.Abs(by[k]), Math.Atan2(p.Y, p.X), Math.Atan2(p.X * q.Y - p.Y * q.X, p.Dot(q)));
            }
            else
            {
                var meets = Meet(Carrier(segments[k], by[k], radius[k]), Carrier(segments[j], by[j], radius[j]));
                if (meets.Count == 0) return (null, [], $"segments {k} and {j} no longer meet");
                end[k] = start[j] = meets.MinBy(m => (m - v).Length);
            }
        }

        var result = new List<Segment>();
        var origins = new List<int>();
        for (var i = 0; i < n; i++)
        {
            switch (segments[i])
            {
                case LineSegment line:
                    if ((end[i] - start[i]).Dot(line.To - line.From) <= tolerance * line.Length) return (null, [], $"segment {i} is used up");
                    result.Add(new LineSegment(start[i], end[i]));
                    break;
                case ArcSegment arc:
                    var (a0, a1) = (Angle(start[i] - arc.Center), Angle(end[i] - arc.Center));
                    var sweep = Math.Sign(arc.Sweep) * Positive(Math.Sign(arc.Sweep) * (a1 - a0));
                    if (Math.Abs(sweep - arc.Sweep) >= Math.PI || radius[i] * Math.Abs(sweep) <= tolerance) return (null, [], $"segment {i} is used up");
                    result.Add(new ArcSegment(arc.Center, radius[i], a0, sweep));
                    break;
                default:
                    throw new NotSupportedException(segments[i].GetType().Name);
            }
            origins.Add(i);
            if (corner[i] is { } round)
            {
                result.Add(round);
                origins.Add(~i);
            }
        }
        return (new ProfileLoop(result), [.. origins], null);
    }

    // The unit direction of travel at a segment's start or end.
    static Vector2D Tangent(Segment s, bool atEnd) => s switch
    {
        LineSegment l => (l.To - l.From).Normalized(),
        ArcSegment a => Along(a.StartAngle + (atEnd ? a.Sweep : 0)) * Math.Sign(a.Sweep),
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    static Vector2D Along(double angle) => new(-Math.Sin(angle), Math.Cos(angle));

    static Vector2D Right(Vector2D t) => new(t.Y, -t.X);

    static double Angle(Vector2D v) => Math.Atan2(v.Y, v.X);

    // An angle in [0, 2π).
    static double Positive(double angle) => angle - 2 * Math.PI * Math.Floor(angle / (2 * Math.PI));

    // A moved segment's whole line or circle: a point and direction, or a centre and radius.
    static (Point2D Point, Vector2D Direction, double Radius) Carrier(Segment s, double by, double radius) => s switch
    {
        LineSegment l => (l.From + Right(Tangent(l, false)) * by, Tangent(l, false), 0),
        ArcSegment a => (a.Center, default, radius),
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    // Where two lines or circles cross (or touch).
    static List<Point2D> Meet((Point2D P, Vector2D D, double R) a, (Point2D P, Vector2D D, double R) b)
    {
        if (a.R == 0 && b.R == 0)
        {
            var cross = a.D.X * b.D.Y - a.D.Y * b.D.X;
            if (Math.Abs(cross) <= Turn) return [];
            var w = b.P - a.P;
            return [a.P + a.D * ((w.X * b.D.Y - w.Y * b.D.X) / cross)];
        }
        if (a.R == 0 || b.R == 0)
        {
            var (line, round) = a.R == 0 ? (a, b) : (b, a);
            // |p + t d − c|² = R²: t² + 2 t (p − c)·d + |p − c|² − R² = 0.
            var w = line.P - round.P;
            var (half, rest) = (w.Dot(line.D), w.Dot(w) - round.R * round.R);
            var disc = half * half - rest;
            if (disc < -1e-12 * round.R * round.R) return [];
            var root = Math.Sqrt(Math.Max(0, disc));
            return [line.P + line.D * (-half - root), line.P + line.D * (-half + root)];
        }
        // Two circles: along the line of centres to the chord, then across it.
        var c = b.P - a.P;
        var dist = c.Length;
        if (dist <= Turn) return [];
        var along = (dist * dist + a.R * a.R - b.R * b.R) / (2 * dist);
        var h2 = a.R * a.R - along * along;
        if (h2 < -1e-12 * a.R * a.R) return [];
        var (u, h) = (c * (1 / dist), Math.Sqrt(Math.Max(0, h2)));
        var mid = a.P + u * along;
        var across = new Vector2D(-u.Y, u.X);
        return [mid + across * h, mid - across * h];
    }
}
