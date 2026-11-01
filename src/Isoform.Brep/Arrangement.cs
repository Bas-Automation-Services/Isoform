using BasAutomation.Geometry;

namespace Isoform.Brep;

/// <summary>Which operand face a result face comes from (operand 0 the first body of a boolean, 1 the second).</summary>
public readonly record struct Origin(int Operand, FaceId Face);

/// <summary>
/// One use of a piece of a curve by a loop, from <see cref="From"/> to <see cref="To"/> (either order), with its
/// p-curve on the loop's surface in the curve's own parameter.
/// </summary>
internal sealed record Use(Curve3 Curve, double From, double To, Curve2 Pcurve)
{
    public Point3D Start => Curve.At(From);
    public Point3D End => Curve.At(To);
    public Point3D Mid => Curve.At((From + To) / 2);
    public Use Reversed() => this with { From = To, To = From };
}

/// <summary>
/// A face being built: a surface, its sense, and loops of uses in the B-rep's convention (the face on the left seen
/// from outside the solid; the first loop the outer one).
/// </summary>
internal sealed record Patch(Surface Surface, bool SameSense, IReadOnlyList<IReadOnlyList<Use>> Loops, IReadOnlySet<Origin> Origins)
{
    /// <summary>The loops with the face on their left in (u, v): the B-rep's own where the face has its surface's sense, reversed where not.</summary>
    public IEnumerable<IReadOnlyList<Use>> UvLoops => Loops.Select(l => Arrangement.Sensed(l, SameSense));

    public Patch Flipped() => this with { SameSense = !SameSense, Loops = [.. Loops.Select(l => Arrangement.Sensed(l, false))] };
}

/// <summary>
/// Faces from uses in a surface's flat (u, v): the uses are traced into loops that keep the face on their left
/// (at every node the next use is the first one clockwise from the way back), and the loops sorted into outer
/// boundaries and the holes inside them.
/// </summary>
internal static class Arrangement
{
    const double Same = 1e-9;

    // Directions closer than this at a node are one direction (tangent uses), told apart by curvature.
    const double Tangent = 1e-7;

    /// <summary>A loop run as given, or reversed (order and direction) when <paramref name="sense"/> is false.</summary>
    public static IReadOnlyList<Use> Sensed(IReadOnlyList<Use> loop, bool sense) =>
        sense ? loop : [.. loop.Reverse().Select(u => u.Reversed())];

    /// <summary>
    /// The faces the uses bound, each an outer loop and its holes, all with the face on their left in (u, v).
    /// Every node must have as many uses leaving it as arriving.
    /// </summary>
    public static List<List<IReadOnlyList<Use>>> Faces(Surface surface, IReadOnlyList<Use> uses)
    {
        var flat = uses.Select(u => Uv.Flat(surface, u.Pcurve, u.From, u.To)).ToList();
        var nodes = new Welded();
        int Node(Point2D p) => nodes[new Point3D(p.X, p.Y, 0)];
        var from = flat.Select(s => Node(s.Start)).ToArray();
        var to = flat.Select(s => Node(s.End)).ToArray();
        var leaving = Enumerable.Range(0, uses.Count).ToLookup(i => from[i]);

        var used = new bool[uses.Count];
        var cycles = new List<List<int>>();
        for (var first = 0; first < uses.Count; first++)
        {
            if (used[first]) continue;
            var cycle = new List<int>();
            var h = first;
            while (!used[h])
            {
                used[h] = true;
                cycle.Add(h);
                h = Next(h);
            }
            if (h != first) throw new BooleanFailure($"a face's edges do not close into loops (at {flat[cycle[^1]].End} in its parameters)");
            cycles.Add(cycle);
        }

        // The first use clockwise from the way back. Uses leaving in one direction (tangent, as a fillet's arc runs off
        // a straight edge) are told apart by curvature: the one bending more to the left is met first. One leaving
        // the way back comes first if it bends right of it, last if left, and the use back itself very last.
        int Next(int h)
        {
            var (backAngle, backCurvature) = Direction(flat[h], atEnd: true);
            var best = -1;
            var (bestTurn, bestCurvature) = (double.MaxValue, 0.0);
            foreach (var c in leaving[to[h]])
            {
                var (angle, curvature) = Direction(flat[c], atEnd: false);
                var turn = Intersections.Wrap(backAngle - angle);
                if (turn > 2 * Math.PI - Tangent) turn -= 2 * Math.PI;
                if (Math.Abs(turn) < Tangent)
                    turn = curvature < backCurvature - Same ? 0 : curvature > backCurvature + Same ? 2 * Math.PI : 4 * Math.PI;
                var better = best < 0 || turn < bestTurn - Tangent || (Math.Abs(turn - bestTurn) <= Tangent && curvature > bestCurvature);
                if (better) (best, bestTurn, bestCurvature) = (c, turn, curvature);
            }
            return best >= 0 ? best : throw new BooleanFailure("a face's edges do not close into loops");
        }

        var loops = cycles.Select(c => (Uses: (IReadOnlyList<Use>)[.. c.Select(i => uses[i])], Loop: new ProfileLoop([.. c.Select(i => flat[i])]))).ToList();
        var outers = loops.Where(l => l.Loop.SignedArea > 0).OrderBy(l => l.Loop.SignedArea).ToList();
        var faces = outers.Select(o => new List<IReadOnlyList<Use>> { o.Uses }).ToList();
        foreach (var hole in loops.Where(l => l.Loop.SignedArea <= 0))
        {
            // Just off the hole on its left, in the face that owns it: the hole's own edges may also bound a face
            // inside it, which a point on them would seem to lie in.
            var p = LeftOf(hole.Loop.Segments[0], 1e-5);
            var owner = outers.FindIndex(o => o.Loop.Winding(p) != 0);
            if (owner < 0) throw new BooleanFailure("a hole of a face lies outside every outer loop");
            faces[owner].Add(hole.Uses);
        }
        return faces;
    }

    /// <summary>The direction a segment leaves its start (or, at its end, the way back along it) and its curvature to the left.</summary>
    static (double Angle, double Curvature) Direction(Segment s, bool atEnd)
    {
        switch (s)
        {
            case LineSegment l:
                var d = atEnd ? l.From - l.To : l.To - l.From;
                return (Math.Atan2(d.Y, d.X), 0);
            case ArcSegment a:
                var sign = Math.Sign(a.Sweep);
                var theta = atEnd ? a.StartAngle + a.Sweep : a.StartAngle;
                var (sin, cos) = Math.SinCos(theta);
                var (tx, ty) = (-sin * sign, cos * sign);
                return atEnd ? (Math.Atan2(-ty, -tx), -sign / a.Radius) : (Math.Atan2(ty, tx), sign / a.Radius);
            case CurveSegment c:
                var t = atEnd ? c.To : c.From;
                var h = c.Heading(t);
                return atEnd ? (Math.Atan2(-h.Y, -h.X), -c.Curvature(t)) : (Math.Atan2(h.Y, h.X), c.Curvature(t));
            default:
                throw new NotSupportedException(s.GetType().Name);
        }
    }

    // The point `distance` to the left of a segment's middle.
    static Point2D LeftOf(Segment s, double distance)
    {
        var m = Midpoint(s);
        var (tx, ty) = s switch
        {
            LineSegment l => ((l.To.X - l.From.X) / l.Length, (l.To.Y - l.From.Y) / l.Length),
            ArcSegment a => (-Math.Sin(a.StartAngle + a.Sweep / 2) * Math.Sign(a.Sweep), Math.Cos(a.StartAngle + a.Sweep / 2) * Math.Sign(a.Sweep)),
            CurveSegment c => Unit(c.Heading((c.From + c.To) / 2)),
            _ => throw new NotSupportedException(s.GetType().Name),
        };
        return new Point2D(m.X - ty * distance, m.Y + tx * distance);
    }

    public static Point2D Midpoint(Segment s) => s switch
    {
        LineSegment l => new((l.From.X + l.To.X) / 2, (l.From.Y + l.To.Y) / 2),
        ArcSegment a => a.PointAt(a.StartAngle + a.Sweep / 2),
        CurveSegment c => c.Mid,
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    static (double, double) Unit(Vector2D v) => (v.X / v.Length, v.Y / v.Length);

    /// <summary>
    /// A point well inside a face given by its loops (face on the left in (u, v)): on a few horizontal lines across
    /// the face, the middle of the widest stretches inside it, keeping the one farthest from every loop. In (u, v).
    /// </summary>
    public static Point2D Inside(Surface surface, IEnumerable<IReadOnlyList<Use>> loops)
    {
        var segments = loops.SelectMany(l => l.Select(u => Uv.Flat(surface, u.Pcurve, u.From, u.To))).ToList();
        var ys = segments.SelectMany(Samples).Select(p => p.Y).ToList();
        var (low, high) = (ys.Min(), ys.Max());
        (Point2D Point, double Clearance)? best = null;
        foreach (var f in new[] { 0.5137, 0.3291, 0.7243, 0.1517, 0.8669, 0.4211, 0.6083 })
        {
            var y = low + (high - low) * f;
            var xs = segments.SelectMany(s => Crossings(s, y)).Order().ToList();
            if (xs.Count % 2 != 0) continue;
            foreach (var (from, to) in Enumerable.Range(0, xs.Count / 2).Select(i => (xs[2 * i], xs[2 * i + 1])).OrderByDescending(s => s.Item2 - s.Item1).Take(3))
            {
                var p = new Point2D((from + to) / 2, y);
                // The distance to the nearest loop, given up once it is no better than the best so far.
                var clearance = double.MaxValue;
                foreach (var s in segments)
                    if ((clearance = Math.Min(clearance, Distances.To(s, p))) <= (best?.Clearance ?? -1)) break;
                if (best is null || clearance > best.Value.Clearance) best = (p, clearance);
            }
        }
        if (best is not { } found) throw new BooleanFailure("no point found inside a face");
        return new Point2D(found.Point.X / Uv.Scale(surface), found.Point.Y / Uv.ScaleV(surface));
    }

    static IEnumerable<Point2D> Samples(Segment s)
    {
        if (s is LineSegment l) return [l.From, l.To];
        if (s is CurveSegment c) return c.Samples();
        var a = (ArcSegment)s;
        return Enumerable.Range(0, 17).Select(i => a.PointAt(a.StartAngle + a.Sweep * i / 16));
    }

    static IEnumerable<double> Crossings(Segment s, double y)
    {
        switch (s)
        {
            case LineSegment l:
                if ((l.From.Y - y) * (l.To.Y - y) < 0) yield return l.From.X + (y - l.From.Y) * (l.To.X - l.From.X) / (l.To.Y - l.From.Y);
                break;
            case ArcSegment a:
                var dy = y - a.Center.Y;
                if (Math.Abs(dy) >= a.Radius) yield break;
                var dx = Math.Sqrt(a.Radius * a.Radius - dy * dy);
                foreach (var x in new[] { dx, -dx })
                    if (a.Covers(Math.Atan2(dy, x))) yield return a.Center.X + x;
                break;
            case CurveSegment c:
                foreach (var x in c.Crossings(y)) yield return x;
                break;
        }
    }
}
