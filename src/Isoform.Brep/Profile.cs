using BasAutomation.Geometry;

namespace Isoform.Brep;

/// <summary>A straight or circular piece of a planar profile, from <see cref="Start"/> to <see cref="End"/>.</summary>
public abstract record Segment
{
    public abstract Point2D Start { get; }
    public abstract Point2D End { get; }

    /// <summary>The same segment run the other way.</summary>
    public abstract Segment Reversed();
}

public sealed record LineSegment(Point2D From, Point2D To) : Segment
{
    public override Point2D Start => From;
    public override Point2D End => To;
    public double Length => (To - From).Length;
    public override Segment Reversed() => new LineSegment(To, From);
}

/// <summary>
/// An arc of the circle round <see cref="Center"/>, from the angle <see cref="StartAngle"/> through
/// <see cref="Sweep"/> radians: counter-clockwise when positive. A sweep of ±2π is a full circle, a loop on its own.
/// </summary>
public sealed record ArcSegment(Point2D Center, double Radius, double StartAngle, double Sweep) : Segment
{
    public override Point2D Start => PointAt(StartAngle);
    public override Point2D End => PointAt(StartAngle + Sweep);
    public bool IsFullCircle => Math.Abs(Math.Abs(Sweep) - 2 * Math.PI) <= 1e-12;
    public Point2D PointAt(double angle) => new(Center.X + Radius * Math.Cos(angle), Center.Y + Radius * Math.Sin(angle));
    public override Segment Reversed() => new ArcSegment(Center, Radius, StartAngle + Sweep, -Sweep);

    /// <summary>A full circle, counter-clockwise from angle 0.</summary>
    public static ArcSegment Circle(Point2D center, double radius) => new(center, radius, 0, 2 * Math.PI);

    /// <summary>Whether the direction at <paramref name="angle"/> lies on the arc (within its sweep).</summary>
    public bool Covers(double angle)
    {
        if (IsFullCircle) return true;
        var d = (angle - StartAngle) * Math.Sign(Sweep);
        d -= 2 * Math.PI * Math.Floor(d / (2 * Math.PI));
        return d <= Math.Abs(Sweep);
    }
}

/// <summary>A closed chain of segments, each ending where the next starts.</summary>
public sealed record ProfileLoop(IReadOnlyList<Segment> Segments)
{
    /// <summary>The enclosed area, positive when the loop runs counter-clockwise (Green's theorem, exact for lines and arcs).</summary>
    public double SignedArea => Segments.Sum(s => s switch
    {
        // A chord's x dy − y dx term, plus for an arc the circular segment between chord and arc.
        LineSegment l => Cross(l.From, l.To) / 2,
        ArcSegment a => Cross(a.Start, a.End) / 2 + Math.Sign(a.Sweep) * a.Radius * a.Radius / 2 * (Math.Abs(a.Sweep) - Math.Sin(Math.Abs(a.Sweep))),
        CurveSegment c => c.AreaTerm(),
        _ => throw new NotSupportedException(),
    });

    /// <summary>The loop run the other way: segments in reverse order, each reversed.</summary>
    public ProfileLoop Reversed() => new([.. Segments.Reverse().Select(s => s.Reversed())]);

    /// <summary>
    /// How many times the loop winds round <paramref name="p"/>: the angle each chord subtends at p, plus ±1 for each
    /// arc whose circular segment (between the arc and its chord) holds p, arcs taken in halves. Exact for points away
    /// from the loop.
    /// </summary>
    public int Winding(Point2D p)
    {
        double angle = 0;
        var turns = 0;
        foreach (var s in Segments)
        {
            if (s is CurveSegment curve)
            {
                angle += curve.Subtended(p);
                continue;
            }
            // An arc in halves: a point on a piece's chord (often, where the chord runs through other vertices) sees
            // that chord at ±π either way and lies on neither side of it, so that piece is halved again. The side of
            // each chord is the exact predicate's, for the angle and the circular segment alike.
            var pieces = new Stack<Segment>(s is ArcSegment { IsFullCircle: false } whole
                ? [whole with { StartAngle = whole.StartAngle + whole.Sweep / 2, Sweep = whole.Sweep / 2 }, whole with { Sweep = whole.Sweep / 2 }]
                : new[] { s });
            while (pieces.TryPop(out var piece))
            {
                var (a, b) = (piece.Start - p, piece.End - p);
                var side = Math.Sign(Predicates.Orient2D(piece.Start, piece.End, p));
                if (side == 0 && a.Dot(b) < 0 && piece is ArcSegment split)
                {
                    pieces.Push(split with { StartAngle = split.StartAngle + split.Sweep / 2, Sweep = split.Sweep / 2 });
                    pieces.Push(split with { Sweep = split.Sweep / 2 });
                    continue;
                }
                angle += side * Math.Atan2(Math.Abs(a.X * b.Y - a.Y * b.X), a.X * b.X + a.Y * b.Y);
                if (piece is ArcSegment arc && InCircularSegment(arc, p)) turns += Math.Sign(arc.Sweep);
            }
        }
        return turns + (int)Math.Round(angle / (2 * Math.PI));
    }

    // Inside the circle and on the arc's side of its chord (a full circle has no chord).
    static bool InCircularSegment(ArcSegment arc, Point2D p)
    {
        if ((p - arc.Center).Length >= arc.Radius) return false;
        if (arc.IsFullCircle) return true;
        var mid = arc.PointAt(arc.StartAngle + arc.Sweep / 2);
        return Math.Sign(Predicates.Orient2D(arc.Start, arc.End, p)) == Math.Sign(Predicates.Orient2D(arc.Start, arc.End, mid));
    }

    static double Cross(Point2D a, Point2D b) => a.X * b.Y - a.Y * b.X;
}

/// <summary>A planar region: an outer loop and holes cut out of it.</summary>
public sealed record Region(ProfileLoop Outer, IReadOnlyList<ProfileLoop> Holes)
{
    public Region(ProfileLoop outer) : this(outer, []) { }

    /// <summary>Outer then holes.</summary>
    public IEnumerable<ProfileLoop> Loops => [Outer, .. Holes];

    /// <summary>
    /// Why this region cannot bound a solid, or null: a loop that does not close, a segment shorter than the
    /// tolerance, two segments that cross or come within the tolerance of each other away from a shared end, a hole
    /// outside the outer loop or inside another hole.
    /// </summary>
    public string? Problem(double tolerance)
    {
        var loops = Loops.ToList();
        for (var l = 0; l < loops.Count; l++)
        {
            var segments = loops[l].Segments;
            var name = l == 0 ? "the outer loop" : $"hole {l}";
            if (segments.Count == 0) return $"{name} is empty";
            for (var i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (s is LineSegment line && line.Length <= tolerance) return $"segment {i} of {name} is shorter than the tolerance";
                if (s is ArcSegment arc && (arc.Radius <= tolerance || arc.Radius * Math.Abs(arc.Sweep) <= tolerance))
                    return $"segment {i} of {name} is an arc smaller than the tolerance";
                if (s is ArcSegment { IsFullCircle: true } && segments.Count > 1) return $"segment {i} of {name} is a full circle, which must be a loop on its own";
                if (s is ArcSegment { Sweep: var sweep } && Math.Abs(sweep) > 2 * Math.PI + 1e-12) return $"segment {i} of {name} sweeps more than a full circle";
                var gap = (s.End - segments[(i + 1) % segments.Count].Start).Length;
                if (gap > tolerance) return $"{name} does not close: segment {i} ends {gap:g3} from where the next starts";
            }
        }

        // Every pair of segments, of all loops, must keep the tolerance apart except where they share an end.
        var all = loops.SelectMany((loop, l) => loop.Segments.Select((s, i) => (Loop: l, Index: i, Segment: s, Count: loop.Segments.Count))).ToList();
        for (var i = 0; i < all.Count; i++)
        for (var j = i + 1; j < all.Count; j++)
        {
            var (a, b) = (all[i], all[j]);
            // Within a loop, a segment shares its end with the next one's start (both ends, in a loop of two).
            var shared = new List<Point2D>();
            if (a.Loop == b.Loop && b.Index == (a.Index + 1) % a.Count) shared.Add(a.Segment.End);
            if (a.Loop == b.Loop && a.Index == (b.Index + 1) % a.Count) shared.Add(a.Segment.Start);
            var distance = Distances.Between(a.Segment, b.Segment, [.. shared], tolerance);
            if (distance <= tolerance)
                return $"segment {a.Index} of {Name(a.Loop)} and segment {b.Index} of {Name(b.Loop)} {(distance == 0 ? "meet" : $"come within {distance:g3} of each other")}";
        }

        if (Math.Abs(Outer.SignedArea) <= tolerance * tolerance) return "the outer loop encloses no area";
        for (var h = 0; h < Holes.Count; h++)
        {
            var p = Holes[h].Segments[0].Start;
            if (Outer.Winding(p) == 0) return $"hole {h + 1} is outside the outer loop";
            for (var k = 0; k < Holes.Count; k++)
                if (k != h && Holes[k].Winding(p) != 0) return $"hole {h + 1} is inside hole {k + 1}";
        }
        return null;

        string Name(int loop) => loop == 0 ? "the outer loop" : $"hole {loop}";
    }
}

/// <summary>The least distance between two segments, leaving out points near shared ends.</summary>
static class Distances
{
    /// <summary>
    /// The least distance between <paramref name="a"/> and <paramref name="b"/>, over candidate points (each end against
    /// the other segment, crossings, and the points where the distance between their carriers is stationary), leaving
    /// out candidates within 10 tolerances of an end the two share.
    /// </summary>
    public static double Between(Segment a, Segment b, Point2D[] shared, double tolerance)
    {
        var best = double.MaxValue;
        void Consider(Point2D p, Segment other)
        {
            if (shared.Any(s => (p - s).Length <= 10 * tolerance)) return;
            best = Math.Min(best, To(other, p));
        }
        foreach (var p in new[] { a.Start, a.End }) Consider(p, b);
        foreach (var p in new[] { b.Start, b.End }) Consider(p, a);
        foreach (var p in Crossings(a, b)) Consider(p, a);
        foreach (var p in Stationary(a, b)) Consider(p, b);
        foreach (var p in Stationary(b, a)) Consider(p, a);
        return best;
    }

    /// <summary>The distance from a point to a segment.</summary>
    public static double To(Segment s, Point2D p) => s switch
    {
        LineSegment l => (p - Closest(l, p)).Length,
        ArcSegment a => a.Covers(Math.Atan2(p.Y - a.Center.Y, p.X - a.Center.X))
            ? Math.Abs((p - a.Center).Length - a.Radius)
            : Math.Min((p - a.Start).Length, (p - a.End).Length),
        CurveSegment c => c.DistanceTo(p),
        _ => throw new NotSupportedException(),
    };

    static Point2D Closest(LineSegment l, Point2D p)
    {
        var d = l.To - l.From;
        var t = Math.Clamp((p - l.From).Dot(d) / d.Dot(d), 0, 1);
        return l.From + d * t;
    }

    // Points on both segments: line–line, line–circle and circle–circle intersections, kept where they lie on both.
    static IEnumerable<Point2D> Crossings(Segment a, Segment b)
    {
        var points = (a, b) switch
        {
            (LineSegment p, LineSegment q) => LineLine(p, q),
            (LineSegment p, ArcSegment q) => LineCircle(p, q),
            (ArcSegment p, LineSegment q) => LineCircle(q, p),
            (ArcSegment p, ArcSegment q) => CircleCircle(p, q),
            _ => [],
        };
        return points.Where(p => On(a, p) && On(b, p));
    }

    // The points of `a` where the distance to b's carrier is stationary: for a circle b, along the line through its
    // centre (the foot on a line, the two points on the line of centres on a circle).
    static IEnumerable<Point2D> Stationary(Segment a, Segment b)
    {
        if (b is not ArcSegment c) yield break;
        switch (a)
        {
            case LineSegment l:
                yield return Closest(l, c.Center);
                break;
            case ArcSegment arc:
                var d = c.Center - arc.Center;
                if (d.Length == 0) yield break; // concentric: the ends cover it
                var u = d / d.Length;
                foreach (var sign in new[] { 1.0, -1.0 })
                {
                    var p = arc.Center + u * (sign * arc.Radius);
                    if (arc.Covers(Math.Atan2(p.Y - arc.Center.Y, p.X - arc.Center.X))) yield return p;
                }
                break;
        }
    }

    static bool On(Segment s, Point2D p) => To(s, p) <= 1e-9 * (1 + Math.Abs(p.X) + Math.Abs(p.Y));

    static Point2D[] LineLine(LineSegment p, LineSegment q)
    {
        var (r, s) = (p.To - p.From, q.To - q.From);
        var denominator = r.X * s.Y - r.Y * s.X;
        if (denominator == 0) return [];
        var w = q.From - p.From;
        var t = (w.X * s.Y - w.Y * s.X) / denominator;
        return [p.From + r * t];
    }

    static Point2D[] LineCircle(LineSegment l, ArcSegment c)
    {
        var d = l.To - l.From;
        var f = l.From - c.Center;
        double a = d.Dot(d), b = 2 * f.Dot(d), k = f.Dot(f) - c.Radius * c.Radius;
        var disc = b * b - 4 * a * k;
        if (disc < 0) return [];
        var root = Math.Sqrt(disc);
        return [l.From + d * ((-b - root) / (2 * a)), l.From + d * ((-b + root) / (2 * a))];
    }

    static Point2D[] CircleCircle(ArcSegment p, ArcSegment q)
    {
        var d = q.Center - p.Center;
        var dist = d.Length;
        if (dist == 0 || dist > p.Radius + q.Radius || dist < Math.Abs(p.Radius - q.Radius)) return [];
        var a = (p.Radius * p.Radius - q.Radius * q.Radius + dist * dist) / (2 * dist);
        var h = Math.Sqrt(Math.Max(0, p.Radius * p.Radius - a * a));
        var mid = p.Center + d * (a / dist);
        var perp = new Vector2D(-d.Y / dist, d.X / dist);
        return [mid + perp * h, mid - perp * h];
    }
}
