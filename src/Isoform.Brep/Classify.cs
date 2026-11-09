using BasAutomation.Geometry;

namespace Isoform.Brep;

internal enum Place { Outside, Boundary, Inside }

/// <summary>Where a point lies against a solid: outside, inside, or on its boundary with the normal pointing the same way or not.</summary>
internal enum Containment { Out, In, OnSame, OnOpposite }

/// <summary>
/// A face in flat (u, v) (see <see cref="Uv"/>): its loops as segments, and the window of u its points are taken in
/// on a cylinder, so that every point of the face has one flat position.
/// </summary>
internal sealed class FlatFace
{
    /// <param name="known">Flat loops (and boxes) already made, by the loop's place in the face.</param>
    public FlatFace(Body body, FaceId id, IReadOnlyDictionary<int, (ProfileLoop Loop, (Point2D, Point2D) Box)>? known = null)
    {
        var face = body[id];
        Surface = face.Surface;
        SameSense = face.SameSense;
        var edges = new List<EdgeId>();
        var seen = new HashSet<EdgeId>();
        var loops = new ProfileLoop[face.Loops.Length];
        boxes = new (Point2D, Point2D)[face.Loops.Length];
        runs = new Run[]?[face.Loops.Length];
        // The windows of a periodic u or v: from the lowest the face's p-curves reach.
        var periodic = Surface.PeriodU is not null || Surface.PeriodV is not null;
        var (uMin, vMin) = (double.MaxValue, double.MaxValue);
        for (var i = 0; i < face.Loops.Length; i++)
        {
            var given = known is not null && known.TryGetValue(i, out var k) ? k : ((ProfileLoop, (Point2D, Point2D))?)null;
            var segments = given is null ? new List<Segment>() : null;
            foreach (var c in body.CoedgesOf(face.Loops[i]))
            {
                var coedge = body[c];
                if (seen.Add(coedge.Edge)) edges.Add(coedge.Edge);
                var (from, to) = body.ParametersOf(c);
                segments?.Add(Uv.Flat(Surface, coedge.Pcurve, from, to));
                if (!periodic) continue;
                var (a, b) = (coedge.Pcurve.At(from), coedge.Pcurve.At(to));
                (uMin, vMin) = (Math.Min(uMin, Math.Min(a.X, b.X)), Math.Min(vMin, Math.Min(a.Y, b.Y)));
            }
            loops[i] = given?.Item1 ?? new ProfileLoop(segments!);
            boxes[i] = given?.Item2 ?? BoxOf(loops[i]);
        }
        (Edges, Loops) = (edges, loops);
        if (Surface.PeriodU is not null) UMin = uMin;
        if (Surface.PeriodV is not null) VMin = vMin;

        // The edges' ends and the points where they turn back along an axis, grown to hold the whole face: a plane,
        // cylinder or cone face lies between its edges (on lines across it); a torus face reaches at most the tube's
        // diameter from them; a sphere face may bulge anywhere on the sphere (a ball's edges are only its seam and poles).
        // A flat face lies within its outer loop.
        var (lo, hi) = (new Point3D(double.MaxValue, double.MaxValue, double.MaxValue), new Point3D(double.MinValue, double.MinValue, double.MinValue));
        void Add(Point3D p) =>
            (lo, hi) = (new Point3D(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z)), new Point3D(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z)));
        IEnumerable<EdgeId> bounding = Surface is Plane ? body.CoedgesOf(face.Loops[0]).Select(c => body[c].Edge) : Edges;
        foreach (var e in bounding)
        {
            var edge = body[e];
            if (edge.Curve is Line3 or Pole)
            {
                Add(edge.Curve.At(edge.T0));
                Add(edge.Curve.At(edge.T1));
            }
            else
                foreach (var p in Extremes(edge)) Add(p);
        }
        if (Surface is Sphere o)
        {
            var r = new Vector3D(o.Radius, o.Radius, o.Radius);
            Add(o.Frame.Origin - r);
            Add(o.Frame.Origin + r);
        }
        var pad = (Surface is Torus t ? 2 * t.Minor : 0) + Intersections.Snap;
        Box = (new Point3D(lo.X - pad, lo.Y - pad, lo.Z - pad), new Point3D(hi.X + pad, hi.Y + pad, hi.Z + pad));
    }

    // The same face in a body whose edges are numbered otherwise.
    FlatFace(FlatFace face, IReadOnlyList<EdgeId> edges)
    {
        (Surface, SameSense, Edges, Loops, UMin, VMin, Box, boxes, runs) = (face.Surface, face.SameSense, edges, face.Loops, face.UMin, face.VMin, face.Box, face.boxes, face.runs);
    }

    /// <summary>This face copied whole into another body, its edges there by <paramref name="edge"/>.</summary>
    public FlatFace Renumbered(Func<EdgeId, EdgeId> edge) => new(this, [.. Edges.Select(edge)]);

    public Surface Surface { get; }
    public bool SameSense { get; }
    public IReadOnlyList<EdgeId> Edges { get; }
    public IReadOnlyList<ProfileLoop> Loops { get; }
    public double UMin { get; }
    public double VMin { get; }
    public (Point3D Min, Point3D Max) Box { get; }

    /// <summary>A box round an edge, grown by twice the snap.</summary>
    public static (Point3D Min, Point3D Max) BoxOf(Edge e)
    {
        var (lo, hi) = (new Point3D(double.MaxValue, double.MaxValue, double.MaxValue), new Point3D(double.MinValue, double.MinValue, double.MinValue));
        void Add(Point3D p) =>
            (lo, hi) = (new Point3D(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z)), new Point3D(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z)));
        if (e.Curve is Line3 or Pole)
        {
            Add(e.Curve.At(e.T0));
            Add(e.Curve.At(e.T1));
        }
        else
            foreach (var p in Extremes(e)) Add(p);
        var pad = 2 * Intersections.Snap;
        return (new Point3D(lo.X - pad, lo.Y - pad, lo.Z - pad), new Point3D(hi.X + pad, hi.Y + pad, hi.Z + pad));
    }

    public static bool Touch((Point3D Min, Point3D Max) a, (Point3D Min, Point3D Max) b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;

    // An edge's ends, and on a circle or ellipse (O + a cos t X + b sin t Y) the points within it where a coordinate
    // is least or greatest: tan t = b Y_i / (a X_i).
    static IEnumerable<Point3D> Extremes(Edge e)
    {
        yield return e.Curve.At(e.T0);
        yield return e.Curve.At(e.T1);
        if (e.Curve is Trace3 trace)
        {
            // Its spans' control points, whose hull holds it.
            foreach (var p in trace.Hull(e.T0, e.T1)) yield return p;
            yield break;
        }
        if (e.Curve is Hyperbola3 h)
        {
            var (fx, fy) = (h.Frame.XAxis, h.Frame.YAxis);
            foreach (var (x, y) in new[] { (fx.X, fy.X), (fx.Y, fy.Y), (fx.Z, fy.Z) })
            {
                var r = -h.Minor * y / (h.Major * x);
                if (Math.Abs(x) > 1e-15 && Math.Abs(r) < 1 && Math.Atanh(r) is var t && t > e.T0 && t < e.T1) yield return h.At(t);
            }
            yield break;
        }
        var (frame, a, b) = e.Curve switch
        {
            Circle3 c => (c.Frame, c.Radius, c.Radius),
            Ellipse3 el => (el.Frame, el.Major, el.Minor),
            _ => (RigidTransform3D.Identity, 0.0, 0.0),
        };
        if (a == 0) yield break;
        foreach (var (x, y) in new[] { (frame.XAxis.X, frame.YAxis.X), (frame.XAxis.Y, frame.YAxis.Y), (frame.XAxis.Z, frame.YAxis.Z) })
        {
            var t = Math.Atan2(b * y, a * x);
            foreach (var s in new[] { t, t + Math.PI })
                if (e.T0 + Intersections.Wrap(s - e.T0) <= e.T1) yield return e.Curve.At(s);
        }
    }

    /// <summary>Whether the ray from p along d meets the face's box.</summary>
    public bool Ahead(Point3D p, Vector3D d)
    {
        var (near, far) = (0.0, double.MaxValue);
        foreach (var (o, v, lo, hi) in new[] { (p.X, d.X, Box.Min.X, Box.Max.X), (p.Y, d.Y, Box.Min.Y, Box.Max.Y), (p.Z, d.Z, Box.Min.Z, Box.Max.Z) })
        {
            if (v == 0)
            {
                if (o < lo || o > hi) return false;
                continue;
            }
            var (t0, t1) = ((lo - o) / v, (hi - o) / v);
            (near, far) = (Math.Max(near, Math.Min(t0, t1)), Math.Min(far, Math.Max(t0, t1)));
        }
        return near <= far;
    }

    /// <summary>A point's flat position on this face (u in the face's window).</summary>
    public Point2D FlatOf(Point3D p) => FlatOf(Uv.Of(Surface, p));

    public Point2D FlatOf(Point2D uv)
    {
        var (du, dv) = ShiftInto(uv);
        return new Point2D((uv.X + du) * Uv.Scale(Surface), (uv.Y + dv) * Uv.ScaleV(Surface));
    }

    /// <summary>The shifts (multiples of 2π) that take (u, v) into the face's windows.</summary>
    public (double U, double V) ShiftInto(Point2D uv) => (
        Surface.PeriodU is null ? 0 : UMin - 1e-9 + Intersections.Wrap(uv.X - (UMin - 1e-9)) - uv.X,
        Surface.PeriodV is null ? 0 : VMin - 1e-9 + Intersections.Wrap(uv.Y - (VMin - 1e-9)) - uv.Y);

    /// <summary>Where a point on (or very near) the face's surface lies against the face.</summary>
    public Place Locate(Point3D p) => Locate(FlatOf(p));

    public Place Locate(Point2D flat)
    {
        // A loop whose box the point is outside is neither near it nor round it.
        var near = Enumerable.Range(0, Loops.Count).Where(l => Within(boxes[l], flat)).ToList();
        foreach (var l in near)
            if (Touches(l, flat)) return Place.Boundary;
        return near.Sum(l => Winding(l, flat)) != 0 ? Place.Inside : Place.Outside;
    }

    // A long loop in runs of consecutive segments, each with a box grown by the snap round all its points (made when
    // first asked; none for a loop with segments of other curves). A run whose box a point is outside is not within the
    // snap of it, and subtends at it the angle of the chord from the run's start to its end: a path in a convex box
    // that does not hold the point cannot turn round it.
    readonly record struct Run(int From, int To, (Point2D Min, Point2D Max) Box);

    const int RunLength = 32;
    readonly Run[]?[] runs = [];

    Run[] RunsOf(int loop) => runs[loop] ??= Runs(Loops[loop].Segments);

    static Run[] Runs(IReadOnlyList<Segment> segments)
    {
        if (segments.Count <= 2 * RunLength || segments.Any(s => s is CurveSegment)) return [];
        var result = new List<Run>();
        for (var from = 0; from < segments.Count; from += RunLength)
        {
            var to = Math.Min(from + RunLength, segments.Count);
            var (lo, hi) = (new Point2D(double.MaxValue, double.MaxValue), new Point2D(double.MinValue, double.MinValue));
            void Add(Point2D p) => (lo, hi) = (new Point2D(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y)), new Point2D(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y)));
            for (var i = from; i < to; i++)
            {
                Add(segments[i].Start);
                Add(segments[i].End);
                // An arc's points farthest along each axis, where it reaches them.
                if (segments[i] is ArcSegment a)
                    for (var k = 0; k < 4; k++)
                        if (a.Covers(k * Math.PI / 2)) Add(a.PointAt(k * Math.PI / 2));
            }
            var pad = Intersections.Snap;
            result.Add(new Run(from, to, (new Point2D(lo.X - pad, lo.Y - pad), new Point2D(hi.X + pad, hi.Y + pad))));
        }
        return [.. result];
    }

    // Whether a loop passes within the snap of a point.
    bool Touches(int loop, Point2D p)
    {
        var segments = Loops[loop].Segments;
        if (RunsOf(loop) is not { Length: > 0 } runs)
            return segments.Any(s => Distances.To(s, p) <= Intersections.Snap);
        foreach (var run in runs)
            if (Within(run.Box, p))
                for (var i = run.From; i < run.To; i++)
                    if (Distances.To(segments[i], p) <= Intersections.Snap) return true;
        return false;
    }

    // How many times a loop winds round a point away from it.
    int Winding(int loop, Point2D p)
    {
        var l = Loops[loop];
        if (RunsOf(loop) is not { Length: > 0 } runs) return l.Winding(p);
        var (angle, turns) = (0.0, 0);
        foreach (var run in runs)
        {
            if (!Within(run.Box, p))
            {
                angle += ProfileLoop.Chord(l.Segments[run.From].Start, l.Segments[run.To - 1].End, p);
                continue;
            }
            var (a, t) = l.Subtends(p, run.From, run.To);
            (angle, turns) = (angle + a, turns + t);
        }
        return turns + (int)Math.Round(angle / (2 * Math.PI));
    }

    // Each loop's box in flat (u, v), grown by the snap and, round a curve's samples, by a tenth of the loop's size.
    (Point2D Min, Point2D Max)[] boxes = [];

    public (Point2D Min, Point2D Max) BoxOfLoop(int loop) => boxes[loop];

    static (Point2D, Point2D) BoxOf(ProfileLoop loop)
    {
        var points = loop.Segments.SelectMany(s => s switch
        {
            CurveSegment c => c.Samples(),
            ArcSegment a => Enumerable.Range(0, 17).Select(i => a.PointAt(a.StartAngle + a.Sweep * i / 16)),
            _ => [s.Start, s.End],
        }).ToList();
        var (lo, hi) = (new Point2D(points.Min(p => p.X), points.Min(p => p.Y)), new Point2D(points.Max(p => p.X), points.Max(p => p.Y)));
        var curved = loop.Segments.Any(s => s is not LineSegment);
        var pad = Intersections.Snap + (curved ? 0.1 * Math.Max(hi.X - lo.X, hi.Y - lo.Y) : 0);
        return (new Point2D(lo.X - pad, lo.Y - pad), new Point2D(hi.X + pad, hi.Y + pad));
    }

    public static bool Within((Point2D Min, Point2D Max) box, Point2D p) => p.X >= box.Min.X && p.X <= box.Max.X && p.Y >= box.Min.Y && p.Y <= box.Max.Y;

    /// <summary>
    /// Whether this face crosses the other's surface at a point inside the other face: on a grid over this face (and
    /// along its loops), wherever the side of the other's surface changes between neighbours that are on this face,
    /// the crossing found by bisection and located on the other face. A crossing finer than the grid can be missed.
    /// </summary>
    public bool Crosses(FlatFace other)
    {
        var (ku, kv) = (Uv.Scale(Surface), Uv.ScaleV(Surface));
        Point3D On(Point2D q) => Surface.At(q.X / ku, q.Y / kv);
        double Side(Point2D q) => Uv.Side(other.Surface, On(q));
        bool Across(Point2D a, Point2D b)
        {
            var (sa, sb) = (Side(a), Side(b));
            if (!(sa * sb < 0) || Math.Abs(sa) <= Intersections.Snap || Math.Abs(sb) <= Intersections.Snap) return false;
            for (var k = 0; k < 60; k++)
            {
                var m = new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                if (Side(m) * sa > 0) a = m; else b = m;
            }
            var p = On(new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2));
            return other.Locate(p) == Place.Inside;
        }

        // Along the loops.
        foreach (var loop in Loops)
        {
            var points = loop.Segments.SelectMany(s => s is CurveSegment c ? c.Samples() : Enumerable.Range(0, 17).Select(i => Along(s, i / 16.0))).ToList();
            for (var i = 0; i + 1 < points.Count; i++)
                if (Across(points[i], points[i + 1])) return true;
        }

        // Across a grid, between neighbours on the face.
        var flat = Loops.SelectMany(l => l.Segments).SelectMany(s => s is CurveSegment c ? c.Samples() : [s.Start, s.End]).ToList();
        var (x0, x1, y0, y1) = (flat.Min(q => q.X), flat.Max(q => q.X), flat.Min(q => q.Y), flat.Max(q => q.Y));
        const int n = 24;
        var grid = new Point2D[n + 1, n + 1];
        var on = new bool[n + 1, n + 1];
        for (var i = 0; i <= n; i++)
        for (var j = 0; j <= n; j++)
        {
            grid[i, j] = new Point2D(x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * j / n);
            on[i, j] = Locate(grid[i, j]) != Place.Outside;
        }
        for (var i = 0; i <= n; i++)
        for (var j = 0; j <= n; j++)
        {
            if (!on[i, j]) continue;
            if (i < n && on[i + 1, j] && Across(grid[i, j], grid[i + 1, j])) return true;
            if (j < n && on[i, j + 1] && Across(grid[i, j], grid[i, j + 1])) return true;
        }
        return false;
    }

    static Point2D Along(Segment s, double f) => s switch
    {
        LineSegment l => new Point2D(l.From.X + (l.To.X - l.From.X) * f, l.From.Y + (l.To.Y - l.From.Y) * f),
        ArcSegment a => a.PointAt(a.StartAngle + a.Sweep * f),
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    /// <summary>The outward normal of the solid at a point of the face.</summary>
    public Vector3D Normal(Point3D p)
    {
        var uv = Uv.Of(Surface, p);
        return Surface.Normal(uv.X, uv.Y) * (SameSense ? 1 : -1);
    }

    public bool Overlaps((Point3D Min, Point3D Max) b) => Touch(Box, b);
}

/// <summary>A body's faces in flat form, for locating points against it.</summary>
internal sealed class Solid(Body body)
{
    // Faces a boolean copied whole into a body, as they were, and holes it carried whole into a face: not flattened again.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Body, Kept> carried = [];

    sealed record Kept(FlatFace?[] Faces, Dictionary<int, Dictionary<int, (ProfileLoop, (Point2D, Point2D))>> Loops);

    /// <summary>
    /// Keeps the flat faces of the faces copied whole into <paramref name="body"/> (null where not), and of the holes
    /// carried whole into its others (by face, then by the hole's place in the face).
    /// </summary>
    public static void Carry(Body body, FlatFace?[] faces, Dictionary<int, Dictionary<int, (ProfileLoop, (Point2D, Point2D))>> loops) =>
        carried.AddOrUpdate(body, new Kept(faces, loops));

    /// <summary>Whether a boolean made the body as an edit of its first operand (which keeps the faces it copied).</summary>
    internal static bool Edited(Body body) => carried.TryGetValue(body, out _);

    public Body Body { get; } = body;
    public FlatFace[] Faces { get; } = [.. Enumerable.Range(0, body.Faces.Length).Select(f =>
        !carried.TryGetValue(body, out var kept) ? new FlatFace(body, new FaceId(f))
        : kept.Faces[f] ?? new FlatFace(body, new FaceId(f), kept.Loops.GetValueOrDefault(f)))];

    /// <summary>A box round the whole solid.</summary>
    public (Point3D Min, Point3D Max) Box => box ??= (
        new Point3D(Faces.Min(f => f.Box.Min.X), Faces.Min(f => f.Box.Min.Y), Faces.Min(f => f.Box.Min.Z)),
        new Point3D(Faces.Max(f => f.Box.Max.X), Faces.Max(f => f.Box.Max.Y), Faces.Max(f => f.Box.Max.Z)));

    (Point3D, Point3D)? box;

    // Ray directions in no special relation to the axes of any part.
    static readonly Vector3D[] Rays =
    [
        new Vector3D(0.5773, 0.6123, 0.5401).Normalized(),
        new Vector3D(-0.3412, 0.8231, -0.4532).Normalized(),
        new Vector3D(0.7071, -0.1829, 0.6833).Normalized(),
        new Vector3D(-0.6131, -0.5507, 0.5664).Normalized(),
        new Vector3D(0.2219, -0.9318, -0.2873).Normalized(),
    ];

    /// <summary>
    /// Where <paramref name="p"/> lies against the solid. On its boundary, <paramref name="normal"/> (the outward
    /// normal of the face p comes from) says whether the two faces point the same way.
    /// </summary>
    public Containment Classify(Point3D p, Vector3D normal)
    {
        foreach (var f in Faces)
        {
            if (Uv.Distance(f.Surface, p) > Intersections.Snap) continue;
            if (f.Locate(p) == Place.Outside) continue;
            return f.Normal(p).Dot(normal) > 0 ? Containment.OnSame : Containment.OnOpposite;
        }
        return Inside(p) ? Containment.In : Containment.Out;
    }

    /// <summary>Whether a point off the boundary is inside: the parity of a ray's crossings, retried when a ray grazes an edge.</summary>
    public bool Inside(Point3D p)
    {
        foreach (var d in Rays)
        {
            var count = 0;
            var clean = true;
            foreach (var f in Faces)
            foreach (var t in f.Ahead(p, d) ? Intersections.Hits(p, d, f.Surface) : [])
            {
                if (t <= Intersections.Snap) continue;
                var q = p + d * t;
                var place = f.Locate(q);
                if (place == Place.Boundary || (place == Place.Inside && Math.Abs(f.Normal(q).Dot(d)) < 1e-6)) clean = false;
                else if (place == Place.Inside) count++;
            }
            if (clean) return count % 2 == 1;
        }
        throw new BooleanFailure("every ray from a point grazes an edge of the other solid");
    }
}

/// <summary>A boolean that cannot be completed, with the reason.</summary>
public sealed class BooleanFailure(string message) : Exception(message);
