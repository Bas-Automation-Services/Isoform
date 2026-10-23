using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Sketching;
using static System.FormattableString;

namespace Isoform.Modeling;

/// <summary>
/// Part edges in a sketch, each named by its two faces: the closed-form intersection of the two surfaces
/// (<see cref="SurfaceIntersection"/>), seen along the sketch plane's normal. A line projects to a line, or to a point
/// seen end-on; a circle parallel to the plane to a circle. An oblique circle or an ellipse would be an ellipse, which a
/// sketch cannot hold: refused, never approximated. The carrier is the whole line or circle, so a face that is split
/// still resolves; only a copy is cut to the edge's extent.
/// </summary>
public static class SketchReferences
{
    /// <summary>The edge between faces <paramref name="a"/> and <paramref name="b"/> as the reference sees it.</summary>
    /// <param name="Curve">The exact 3D curve; null when the reference does not resolve.</param>
    /// <param name="Carrier">The curve in sketch coordinates; null for a line seen end-on (it projects to a point) or a problem.</param>
    public sealed record Projection(Curve? Curve, SketchCarrier? Carrier, string? Problem);

    /// <summary>
    /// Projects the edge between faces <paramref name="a"/> and <paramref name="b"/> of <paramref name="surfaces"/> into
    /// <paramref name="frame"/>'s plane, the branch through <paramref name="seed"/> where two surfaces meet twice. A
    /// face that is not there, or names surfaces that differ, is a problem; so is an ellipse.
    /// </summary>
    public static Projection Project(string a, string b, IReadOnlyList<Surface> surfaces, Frame frame, Point3D seed)
    {
        var (sa, problemA) = Face(a, surfaces);
        var (sb, problemB) = Face(b, surfaces);
        if ((problemA ?? problemB) is { } problem) return new(null, null, problem);
        var edge = $"the edge between {a} and {b}";
        if (!sa!.Analytic || !sb!.Analytic)
            return new(null, null, $"{edge} has no closed form: {(sa.Analytic ? b : a)} is a {(sa.Analytic ? sb! : sa).Kind.ToString().ToLowerInvariant()} face");
        switch (SurfaceIntersection.Curve(sa, sb, seed))
        {
            case Line l:
                var (o, d) = (frame.ToLocal(l.Origin), frame.ToLocal(l.Direction));
                var length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
                return new(l, length < 1e-9 ? null : new CarrierLine(o.X, o.Y, d.X / length, d.Y / length), null);
            case Circle c when c.Normal.Cross(frame.Z).Length <= 1e-9:
                var m = frame.ToLocal(c.Center);
                return new(c, new CarrierCircle(m.X, m.Y, c.Radius), null);
            case Circle c:
                var tilt = Math.Acos(Math.Min(1, Math.Abs(c.Normal.Normalized().Dot(frame.Z)))) * 180 / Math.PI;
                return new(null, null, Invariant($"{edge} is a circle at {tilt:0.#}° to the sketch plane: it projects to an ellipse, which a sketch cannot hold"));
            case Ellipse:
                return new(null, null, $"{edge} is an ellipse (a plane cutting a cylinder aslant): a sketch cannot hold one");
            default:
                return new(null, null, $"{a} and {b} meet in no line or circle: they do not meet, or not in closed form");
        }
    }

    /// <summary>
    /// Every projected entity of <paramref name="sketch"/> on <paramref name="plane"/>, resolved against the surfaces of
    /// the part where the sketch stands: carriers by entity id, and for each that does not resolve (a face gone or
    /// ambiguous, an ellipse now, a projection of another kind than the entity) why. Each seeds its branch at its own
    /// first point.
    /// </summary>
    public static (IReadOnlyDictionary<string, SketchCarrier> Carriers, IReadOnlyList<(string Entity, string Problem)> Lost) Resolve(
        Sketch sketch, SketchPlane plane, IReadOnlyList<Surface> surfaces)
    {
        var frame = Profiles.FrameOf(plane);
        var carriers = new Dictionary<string, SketchCarrier>(StringComparer.Ordinal);
        var lost = new List<(string, string)>();
        foreach (var e in sketch.Entities.Where(e => e.Project is not null))
        {
            var at = sketch.Points[e switch { SketchLine l => l.From, SketchArc k => k.From, SketchCircle k => k.Center, _ => throw new ArgumentException(e.Id) }];
            var seed = frame.ToWorld(new Point3D(at[0] + (e is SketchCircle k2 ? k2.Radius : 0), at[1], 0));
            var p = Project(e.Project![0], e.Project[1], surfaces, frame, seed);
            var problem = p.Problem ?? (p.Carrier, e) switch
            {
                (null, _) => $"the edge between {e.Project[0]} and {e.Project[1]} is seen end-on now: it projects to a point",
                (CarrierLine, SketchLine) or (CarrierCircle, SketchArc or SketchCircle) => null,
                (CarrierLine, _) => $"the edge between {e.Project[0]} and {e.Project[1]} projects to a line now, not to the {(e is SketchArc ? "arc" : "circle")} it was",
                _ => $"the edge between {e.Project[0]} and {e.Project[1]} projects to a circle now, not to the line it was",
            };
            if (problem is null) carriers[e.Id] = p.Carrier!;
            else lost.Add((e.Id, problem));
        }
        return (carriers, lost);
    }

    // Field values this small are on the boundary; a face is looked for this far either side of an edge, in mm.
    const double Touch = 1e-9;
    const double Aside = 1e-4;

    /// <summary>
    /// The edge between faces <paramref name="a"/> and <paramref name="b"/> of a compiled part as pieces to copy into a
    /// sketch on <paramref name="frame"/>'s plane: the carrier cut to where the two faces really meet on the part, every
    /// piece of it or, with <paramref name="near"/>, the one nearest that point. The ends are where the curve crosses
    /// the face that stops it, exactly. An edge that cannot be projected throws an <see cref="ArgumentException"/> saying why.
    /// </summary>
    public static IReadOnlyList<ProjectedPiece> Copy(CompiledPart part, string a, string b, Frame frame, Point3D? near = null)
    {
        var box = (Min: part.Min, Max: part.Max);
        var seed = near ?? new Point3D((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2, (box.Min.Z + box.Max.Z) / 2);
        var p = Project(a, b, part.Surfaces, frame, seed);
        if (p.Problem is not null) throw new ArgumentException(p.Problem);
        var (sa, sb) = (part.Surfaces.Last(s => s.Source == a), part.Surfaces.Last(s => s.Source == b));
        var curve = p.Curve!;
        var body = part.Bodies.FirstOrDefault(k => k.Field.SampleSurface(sa.Id, seed) is not null && k.Field.SampleSurface(sb.Id, seed) is not null)?.Field ?? part.Field!;

        // Where the curve is an edge: on the boundary, with each face there on one side of it.
        bool Faces(Surface s, Point3D q, Vector3D along)
        {
            var aside = along.Cross(s.Gradient(q)).Normalized();
            foreach (var sign in new[] { 1.0, -1.0 })
            {
                var r = q + aside * (sign * Aside);
                for (var k = 0; k < 2; k++) r -= s.Gradient(r) * s.Distance(r);
                var sample = body.Sample(r);
                if (Math.Abs(sample.Value) <= Touch && part.Surfaces[sample.Owner].Source == s.Source) return true;
            }
            return false;
        }
        bool On(double t)
        {
            var q = curve.At(t);
            if (Math.Abs(body.Value(q)) > Touch) return false;
            var along = curve.Tangent(t).Normalized();
            return Faces(sa, q, along) && Faces(sb, q, along);
        }

        // Sampled along the circle, or the line where it crosses the part's box, then each run's ends found by halving.
        var closed = curve is Circle;
        var (t0, t1) = closed ? (0.0, 2 * Math.PI) : Span((Line)curve, box.Min, box.Max);
        var length = closed ? 2 * Math.PI * ((Circle)curve).Radius : t1 - t0;
        var n = (int)Math.Clamp(Math.Ceiling(length / 0.05), 64, 4096);
        double T(int i) => t0 + (t1 - t0) * i / n;
        var on = Enumerable.Range(0, closed ? n : n + 1).Select(i => On(T(i))).ToArray();
        var runs = Runs(on, closed);
        if (runs.Count == 0) throw new ArgumentException($"{a} and {b} do not meet in an edge of the part");

        double End(int inside, int outside)
        {
            var (tin, tout) = (T(inside), T(outside));
            for (var k = 0; k < 50; k++)
            {
                var mid = (tin + tout) / 2;
                if (On(mid)) tin = mid;
                else tout = mid;
            }
            return tin;
        }
        // The halved end, moved onto the face that stops the edge there.
        Point3D Snap(double t)
        {
            var q = curve.At(t);
            return part.Surfaces.Where(s => s.Analytic && s.Source != a && s.Source != b && Math.Abs(s.Distance(q)) <= 10 * Aside)
                .Select(s => SurfaceIntersection.Cross(curve, s, q)).Where(c => c is { } x && (x.Point - q).Length <= 10 * Aside)
                .Select(c => c!.Value.Point).OrderBy(x => (x - q).Length).FirstOrDefault(q);
        }
        double[] Local(Point3D q) => [frame.ToLocal(q).X, frame.ToLocal(q).Y];

        var pieces = new List<(ProjectedPiece Piece, double Distance)>();
        foreach (var (first, last) in runs)
        {
            var distance = near is { } at ? Enumerable.Range(first, last - first + 1).Min(i => (curve.At(T(i % n)) - at).Length) : 0;
            if (p.Carrier is null)
            {
                pieces.Add((new ProjectedPiece(null, Local(curve.At(T(first)))), distance));
                continue;
            }
            if (closed && last - first + 1 >= n)
            {
                pieces.Add((new ProjectedPiece(p.Carrier), distance));
                continue;
            }
            var (from, to) = (Snap(first > 0 || closed ? End(first, first - 1) : T(0)), Snap(last < on.Length - 1 || closed ? End(last, last + 1) : T(last)));
            // An arc runs counter-clockwise in the sketch: the curve's way round when it faces the same way as the plane.
            if (p.Carrier is CarrierCircle && ((Circle)curve).Normal.Dot(frame.Z) < 0) (from, to) = (to, from);
            pieces.Add((new ProjectedPiece(p.Carrier, Local(from), Local(to)), distance));
        }
        return near is null ? [.. pieces.Select(x => x.Piece)] : [pieces.MinBy(x => x.Distance).Piece];
    }

    /// <summary>The runs of trues, as first and last index; round a closed curve a run may wrap past the end.</summary>
    static List<(int First, int Last)> Runs(bool[] on, bool closed)
    {
        var runs = new List<(int, int)>();
        var n = on.Length;
        if (on.All(x => x)) return [(0, n - 1)];
        // Round a closed curve, start after an off sample so no run is cut in two.
        var start = closed ? Array.IndexOf(on, false) + 1 : 0;
        for (var i = 0; i < n; i++)
        {
            if (!on[(start + i) % n]) continue;
            var first = i;
            while (i + 1 < n && on[(start + i + 1) % n]) i++;
            runs.Add((start + first, start + i));
        }
        return runs;
    }

    /// <summary>Where the line crosses the box grown by a millimetre, as parameters along it.</summary>
    static (double T0, double T1) Span(Line line, Point3D min, Point3D max)
    {
        var (t0, t1) = (double.NegativeInfinity, double.PositiveInfinity);
        foreach (var (o, d, lo, hi) in new[] { (line.Origin.X, line.Direction.X, min.X, max.X), (line.Origin.Y, line.Direction.Y, min.Y, max.Y), (line.Origin.Z, line.Direction.Z, min.Z, max.Z) })
        {
            if (Math.Abs(d) < 1e-12) continue;
            var (u, v) = ((lo - 1 - o) / d, (hi + 1 - o) / d);
            (t0, t1) = (Math.Max(t0, Math.Min(u, v)), Math.Min(t1, Math.Max(u, v)));
        }
        return (t0, Math.Max(t0, t1));
    }

    /// <summary>The surface a face name stands for here; null with why when no surface has it or it names several.</summary>
    static (Surface? Surface, string? Problem) Face(string name, IReadOnlyList<Surface> surfaces)
    {
        var named = surfaces.Where(s => s.Source == name).ToList();
        if (named.Count == 0) return (null, $"{name} is not a face of the part here");
        if (named.Any(s => !ReferenceEquals(s, named[^1]) && !s.SameGeometry(named[^1], 1e-9)))
            return (null, $"{name} is ambiguous here: it names {named.Count} different surfaces");
        return (named[^1], null);
    }
}
