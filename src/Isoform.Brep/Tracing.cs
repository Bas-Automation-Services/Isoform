using System.Collections.Immutable;
using BasAutomation.Geometry;

namespace Isoform.Brep;

/// <summary>
/// A curve two surfaces share that has no closed form (two cylinders at an angle, a cylinder across a cone, a plane at
/// an angle to a torus): a cubic Hermite spline through points on both surfaces, with the curve's tangents there,
/// traced along it by <see cref="Tracing"/> to within <see cref="Tracing.Accuracy"/> of the curve itself. The knots
/// run by chord length; a closed one goes round once as t runs from 0 to 2π (its last point its first), an open one
/// (cut off where it leaves the region it was traced in) from 0 to its length.
/// </summary>
public sealed record Trace3(Surface A, Surface B, ImmutableArray<double> Knots, ImmutableArray<Point3D> Points, ImmutableArray<Vector3D> Derivatives, bool Closed) : Curve3
{
    public override double? Period => Closed ? 2 * Math.PI : null;

    /// <summary>The last knot: 2π on a closed curve.</summary>
    public double End => Knots[^1];

    /// <summary>|dC/dt|, the same everywhere but for rounding (1 on an open curve, its length over 2π on a closed one).</summary>
    public double Speed => Derivatives[0].Length;

    /// <summary>The curve itself at t: the spline's point moved onto both surfaces (to rounding), smooth where the spline is not.</summary>
    public Point3D Exact(double t) => Tracing.Project(A, B, At(t), End * Speed) ?? At(t);

    /// <summary>How far a point is off the curve's two surfaces (the farther).</summary>
    public double Gap(Point3D p) => Math.Max(Uv.Distance(A, p), Uv.Distance(B, p));

    // The span holding t and t itself, taken round into [0, 2π) on a closed curve.
    (int Span, double T) Locate(double t)
    {
        if (Closed) t -= 2 * Math.PI * Math.Floor(t / (2 * Math.PI));
        var i = ImmutableArray.BinarySearch(Knots, t);
        if (i < 0) i = ~i - 1;
        return (Math.Clamp(i, 0, Knots.Length - 2), t);
    }

    public override Point3D At(double t)
    {
        var (i, x) = Locate(t);
        // Straight on past an open curve's ends.
        if (x < Knots[0]) return Points[0] + Derivatives[0] * (x - Knots[0]);
        if (x > End) return Points[^1] + Derivatives[^1] * (x - End);
        var h = Knots[i + 1] - Knots[i];
        var s = (x - Knots[i]) / h;
        var (s2, s3) = (s * s, s * s * s);
        return Points[i] + (Points[i + 1] - Points[i]) * (3 * s2 - 2 * s3) + Derivatives[i] * (h * (s3 - 2 * s2 + s)) + Derivatives[i + 1] * (h * (s3 - s2));
    }

    public override Vector3D Derivative(double t)
    {
        var (i, x) = Locate(t);
        if (x < Knots[0]) return Derivatives[0];
        if (x > End) return Derivatives[^1];
        var h = Knots[i + 1] - Knots[i];
        var s = (x - Knots[i]) / h;
        return (Points[i + 1] - Points[i]) * ((6 * s - 6 * s * s) / h) + Derivatives[i] * (3 * s * s - 4 * s + 1) + Derivatives[i + 1] * (3 * s * s - 2 * s);
    }

    /// <summary>The span's Bézier control points (a cubic Hermite piece is one): the curve lies in their hull.</summary>
    public (Point3D P0, Point3D P1, Point3D P2, Point3D P3) Bezier(int i)
    {
        var h = Knots[i + 1] - Knots[i];
        return (Points[i], Points[i] + Derivatives[i] * (h / 3), Points[i + 1] - Derivatives[i + 1] * (h / 3), Points[i + 1]);
    }

    /// <summary>
    /// The spans over [t0, t1] (t0 &lt; t1; on a closed curve as far round as t1 reaches): each span's index, the part of
    /// it covered, and how far its knots are shifted (whole turns) to lie there.
    /// </summary>
    public IEnumerable<(int Span, double From, double To, double Shift)> Spans(double t0, double t1)
    {
        var turn = Closed ? 2 * Math.PI * Math.Floor(t0 / (2 * Math.PI)) : 0;
        var (i, _) = Locate(t0);
        while (true)
        {
            var (a, b) = (Knots[i] + turn, Knots[i + 1] + turn);
            var (from, to) = (Math.Max(a, t0), Math.Min(b, t1));
            if (to > from || t0 == t1) yield return (i, from, to, turn);
            if (b >= t1) yield break;
            if (++i == Knots.Length - 1)
            {
                if (!Closed) yield break;
                (i, turn) = (0, turn + 2 * Math.PI);
            }
        }
    }

    /// <summary>The control points of every span over [t0, t1]: a box round them holds that piece of the curve.</summary>
    public IEnumerable<Point3D> Hull(double t0, double t1)
    {
        foreach (var (i, _, _, _) in Spans(t0, t1))
        {
            var (p0, p1, p2, p3) = Bezier(i);
            yield return p0;
            yield return p1;
            yield return p2;
            yield return p3;
        }
    }

    /// <summary>The parameter of the point nearest <paramref name="p"/> on [t0, t1] (the whole curve by default), and its distance.</summary>
    public (double T, double Distance) Nearest(Point3D p, double? t0 = null, double? t1 = null)
    {
        var (from, to) = (t0 ?? Knots[0], t1 ?? End);
        // Spans in order of how near their control points' box comes, each searched until none can come nearer.
        var spans = Spans(from, to).Select(s =>
        {
            var (p0, p1, p2, p3) = Bezier(s.Span);
            var lo = new Point3D(Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X)), Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y)), Math.Min(Math.Min(p0.Z, p1.Z), Math.Min(p2.Z, p3.Z)));
            var hi = new Point3D(Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X)), Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y)), Math.Max(Math.Max(p0.Z, p1.Z), Math.Max(p2.Z, p3.Z)));
            var dx = Math.Max(0, Math.Max(lo.X - p.X, p.X - hi.X));
            var dy = Math.Max(0, Math.Max(lo.Y - p.Y, p.Y - hi.Y));
            var dz = Math.Max(0, Math.Max(lo.Z - p.Z, p.Z - hi.Z));
            return (s.From, s.To, Bound: Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }).OrderBy(s => s.Bound).ToList();
        var (best, bestT) = (double.MaxValue, from);
        foreach (var (a0, b0, bound) in spans)
        {
            if (bound >= best) break;
            // A span is short and nearly straight: the distance has one minimum along it.
            double D(double t) => (At(t) - p).LengthSquared;
            const double g = 0.6180339887498949;
            var (a, b) = (a0, b0);
            var (x1, x2) = (b - g * (b - a), a + g * (b - a));
            var (f1, f2) = (D(x1), D(x2));
            for (var k = 0; k < 60 && b - a > 1e-15 * (1 + Math.Abs(a)); k++)
            {
                if (f1 < f2)
                {
                    (b, x2, f2) = (x2, x1, f1);
                    x1 = b - g * (b - a);
                    f1 = D(x1);
                }
                else
                {
                    (a, x1, f1) = (x1, x2, f2);
                    x2 = a + g * (b - a);
                    f2 = D(x2);
                }
            }
            foreach (var (t, f) in new[] { (a0, D(a0)), (b0, D(b0)), (x1, f1), (x2, f2) })
                if (Math.Sqrt(f) < best) (best, bestT) = (Math.Sqrt(f), t);
        }
        return (bestT, best);
    }
}

/// <summary>
/// A <see cref="Trace3"/> in a surface's (u, v), with the curve's own parameter: the point at θ = Offset + Sense · t,
/// as the surface's (u, v), shifted by (DU, DV). Angles run on continuously: each lies within half a turn of
/// <see cref="Track"/>, the curve's (u, v) at its knots unwrapped along it.
/// </summary>
public sealed record Trace2(Surface Surface, Trace3 Curve, ImmutableArray<Point2D> Track, double Offset, int Sense, double DU, double DV) : Curve2
{
    public static Trace2 Of(Surface s, Trace3 c)
    {
        var track = ImmutableArray.CreateBuilder<Point2D>(c.Points.Length);
        foreach (var p in c.Points)
        {
            var uv = Uv.Of(s, p);
            if (track.Count > 0)
            {
                var last = track[^1];
                if (s.PeriodU is not null) uv = new Point2D(uv.X + 2 * Math.PI * Math.Round((last.X - uv.X) / (2 * Math.PI)), uv.Y);
                if (s.PeriodV is not null) uv = new Point2D(uv.X, uv.Y + 2 * Math.PI * Math.Round((last.Y - uv.Y) / (2 * Math.PI)));
            }
            track.Add(uv);
        }
        return new Trace2(s, c, track.MoveToImmutable(), 0, 1, 0, 0);
    }

    // The tracked (u, v) at θ: between the knots either side, a whole turn on for each time a closed curve has gone round.
    Point2D Reference(double theta)
    {
        var knots = Curve.Knots;
        var turns = Curve.Closed ? Math.Floor(theta / (2 * Math.PI)) : 0;
        var x = Math.Clamp(theta - 2 * Math.PI * turns, knots[0], knots[^1]);
        var i = ImmutableArray.BinarySearch(knots, x);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, knots.Length - 2);
        var f = (x - knots[i]) / (knots[i + 1] - knots[i]);
        var (a, b) = (Track[i], Track[i + 1]);
        var wind = Track[^1] - Track[0];
        return new Point2D(a.X + (b.X - a.X) * f + wind.X * turns, a.Y + (b.Y - a.Y) * f + wind.Y * turns);
    }

    // The point's (u, v), unshifted.
    Point2D Unshifted(double theta)
    {
        var uv = Uv.Of(Surface, Curve.At(theta));
        var near = Reference(theta);
        var u = Surface.PeriodU is null ? uv.X : uv.X + 2 * Math.PI * Math.Round((near.X - uv.X) / (2 * Math.PI));
        var v = Surface.PeriodV is null ? uv.Y : uv.Y + 2 * Math.PI * Math.Round((near.Y - uv.Y) / (2 * Math.PI));
        return new Point2D(u, v);
    }

    public override Point2D At(double t)
    {
        var uv = Unshifted(Offset + Sense * t);
        return new Point2D(uv.X + DU, uv.Y + DV);
    }

    // C' = Su u' + Sv v', solved in the least squares (C' lies in the tangent plane but for the spline's own error).
    public override Vector2D Derivative(double t)
    {
        var theta = Offset + Sense * t;
        var uv = Unshifted(theta);
        var d = Curve.Derivative(theta) * Sense;
        var (su, sv) = (Surface.DerivativeU(uv.X, uv.Y), Surface.DerivativeV(uv.X, uv.Y));
        var (a, b, c) = (su.Dot(su), su.Dot(sv), sv.Dot(sv));
        var (r, s) = (su.Dot(d), sv.Dot(d));
        var det = a * c - b * b;
        return new Vector2D((c * r - b * s) / det, (a * s - b * r) / det);
    }
}

/// <summary>
/// Curves two surfaces share without a closed form, traced: points on lines of one surface (a ruled one: plane,
/// cylinder or cone) where they cross the other start each branch; from there it is followed both ways by stepping
/// along the tangent (the normals' cross product) and projecting back onto both surfaces, each step as long as the
/// spline through the points stays within <see cref="Accuracy"/> of the curve halfway. A branch ends where it comes
/// round to its start, or where it leaves the region (twice the box it was asked for).
/// </summary>
internal static class Tracing
{
    /// <summary>How far the spline may stray from the curve between its points (mm).</summary>
    public const double Accuracy = 1e-10;

    // Lines of the ruled surface laid across the region, per family.
    const int Lines = 192;

    /// <summary>
    /// The curves surfaces a and b share that pass through the box, followed as far as <paramref name="reach"/> (all
    /// the way round where they close within it), or why there are none to be had. Curves in <paramref name="known"/>
    /// (traced before along the same surfaces) are not traced again; those found are added to it.
    /// </summary>
    public static SurfaceMeeting Trace(Surface a, Surface b, (Point3D Min, Point3D Max) box, (Point3D Min, Point3D Max) reach, List<Trace3> known)
    {
        if (!IsRuled(a) && !IsRuled(b)) return SurfaceMeeting.Refused($"a {Name(a)} against a {Name(b)}, neither ruled");
        var size = (box.Max - box.Min).Length;
        var span = (reach.Max - reach.Min).Length;
        var centre = box.Min + (box.Max - box.Min) / 2;
        foreach (var seed in Seeds(a, b, centre, size).Concat(Seeds(b, a, centre, size)))
        {
            if (!Within(box, seed, Intersections.Snap)) continue;
            if (Project(a, b, seed, size) is not { } p) continue;
            if (known.Any(t => t.Nearest(p).Distance <= Intersections.Snap)) continue;
            var trace = Follow(a, b, p, reach, span);
            if (trace is null) return SurfaceMeeting.Refused($"a {Name(a)} and a {Name(b)} that touch, or meet where the curve cannot be followed");
            known.Add(trace);
        }
        // Those that come into the box.
        var near = known.Where(t =>
        {
            var hull = t.Hull(t.Knots[0], t.End).ToList();
            return hull.Min(p => p.X) <= box.Max.X && hull.Max(p => p.X) >= box.Min.X && hull.Min(p => p.Y) <= box.Max.Y && hull.Max(p => p.Y) >= box.Min.Y
                   && hull.Min(p => p.Z) <= box.Max.Z && hull.Max(p => p.Z) >= box.Min.Z;
        }).ToArray();
        return near.Length == 0 ? SurfaceMeeting.Nothing : SurfaceMeeting.Along(near);
    }

    static string Name(Surface s) => s.GetType().Name.ToLowerInvariant();

    static bool IsRuled(Surface s) => s is Plane or Cylinder or Cone;

    static bool Within((Point3D Min, Point3D Max) box, Point3D p, double pad) =>
        p.X >= box.Min.X - pad && p.X <= box.Max.X + pad && p.Y >= box.Min.Y - pad && p.Y <= box.Max.Y + pad && p.Z >= box.Min.Z - pad && p.Z <= box.Max.Z + pad;

    // Where lines of the ruled surface, laid across the region round the centre, cross the other surface.
    static IEnumerable<Point3D> Seeds(Surface ruled, Surface other, Point3D centre, double size)
    {
        IEnumerable<(Point3D Origin, Vector3D Direction)> lines;
        switch (ruled)
        {
            case Plane p:
            {
                var n = p.Frame.ZAxis;
                var foot = centre - n * (centre - p.Frame.Origin).Dot(n);
                lines = new[] { p.Frame.XAxis, p.Frame.YAxis }.SelectMany(d => Enumerable.Range(0, Lines + 1)
                    .Select(k => (foot + n.Cross(d) * (size * ((double)k / Lines - 0.5)), d)));
                break;
            }
            case Cylinder c:
            {
                var h = (centre - c.Frame.Origin).Dot(c.Frame.ZAxis);
                lines = Enumerable.Range(0, Lines).Select(k => (c.At(2 * Math.PI * k / Lines, h), c.Frame.ZAxis));
                break;
            }
            case Cone k:
                lines = Enumerable.Range(0, Lines).Select(i =>
                {
                    var (sin, cos) = Math.SinCos(2 * Math.PI * i / Lines);
                    var radial = k.Frame.XAxis * cos + k.Frame.YAxis * sin;
                    return (k.Frame.Origin + radial * k.Radius, (radial * k.Slope + k.Frame.ZAxis).Normalized());
                });
                break;
            default:
                yield break;
        }
        foreach (var (origin, direction) in lines)
        foreach (var t in Intersections.Hits(origin, direction, other))
        {
            var q = origin + direction * t;
            // Not on a cone's other nappe, past its apex.
            if (ruled is Cone cone && cone.RadiusAt((q - cone.Frame.Origin).Dot(cone.Frame.ZAxis)) <= Intersections.Snap) continue;
            yield return q;
        }
    }

    // Steps from the seed along the curve both ways until it closes or leaves the region; null where it cannot be followed.
    static Trace3? Follow(Surface a, Surface b, Point3D seed, (Point3D Min, Point3D Max) reach, double size)
    {
        var forward = March(a, b, seed, 1, reach, size);
        if (forward is null) return null;
        List<(Point3D P, Vector3D T)> points;
        if (forward.Value.Closed) points = forward.Value.Points;
        else
        {
            var back = March(a, b, seed, -1, reach, size);
            if (back is null || back.Value.Closed) return null;
            points = [.. back.Value.Points.AsEnumerable().Reverse().Select(x => (x.P, -x.T)).SkipLast(1), .. forward.Value.Points];
        }
        var closed = forward.Value.Closed;
        var knots = new double[points.Count];
        for (var i = 1; i < points.Count; i++) knots[i] = knots[i - 1] + (points[i].P - points[i - 1].P).Length;
        // A closed curve's knots scaled to run round in 2π, its tangents lengthened to match.
        var scale = closed ? 2 * Math.PI / knots[^1] : 1;
        if (closed) knots[^1] = 2 * Math.PI;
        return new Trace3(a, b, [.. knots.Select((k, i) => i == knots.Length - 1 && closed ? k : k * scale)], [.. points.Select(x => x.P)],
            [.. points.Select(x => x.T / scale)], closed);
    }

    // The points (with unit tangents along the way taken) from the seed in one direction of a × b's normals.
    static (List<(Point3D P, Vector3D T)> Points, bool Closed)? March(Surface a, Surface b, Point3D seed, int sense, (Point3D Min, Point3D Max) reach, double size)
    {
        if (Tangent(a, b, seed) is not { } t0) return null;
        var points = new List<(Point3D P, Vector3D T)> { (seed, t0 * sense) };
        var (h, travelled) = (size / 64, 0.0);
        var (least, most) = (size * 1e-9, size / 16);
        while (points.Count < 200_000)
        {
            var (p, t) = points[^1];
            // Round to the start: the last step lands on it.
            var back = seed - p;
            if (points.Count > 3 && back.Length <= 1.5 * h && back.Dot(t) > 0 && travelled > 2 * back.Length)
            {
                if (Error(a, b, p, t, seed, points[0].T, size) <= Accuracy)
                {
                    points.Add((seed, points[0].T));
                    return (points, true);
                }
                // Too long a last step: shorter ones up to it.
                h = Math.Min(h, back.Length / 2);
            }
            var stepped = false;
            while (h >= least)
            {
                if (Project(a, b, p + t * h, size) is not { } q || Tangent(a, b, q) is not { } tq)
                {
                    h /= 2;
                    continue;
                }
                tq *= sense;
                var chord = q - p;
                // Not round a turn too sharp to follow, nor onto another branch.
                if (t.Dot(tq) < Math.Cos(0.2) || chord.Dot(t) < 0.5 * h || chord.Length > 2 * h)
                {
                    h /= 2;
                    continue;
                }
                var error = Error(a, b, p, t, q, tq, size);
                if (error > Accuracy)
                {
                    h /= 2;
                    continue;
                }
                points.Add((q, tq));
                travelled += chord.Length;
                if (error < Accuracy / 16) h = Math.Min(most, h * 1.5);
                stepped = true;
                break;
            }
            if (!stepped) return null;
            if (!Within(reach, points[^1].P, 0)) return (points, false);
        }
        return null;
    }

    // How far the spline from p to q (unit tangents, the chord as the span) strays from the curve halfway.
    static double Error(Surface a, Surface b, Point3D p, Vector3D t, Point3D q, Vector3D tq, double size)
    {
        var length = (q - p).Length;
        var mid = p + (q - p) / 2 + (t - tq) * (length / 8);
        return Project(a, b, mid, size) is { } m ? (m - mid).Length : double.MaxValue;
    }

    /// <summary>The curve's direction at a point on both surfaces, along a's normal × b's; null where they touch.</summary>
    static Vector3D? Tangent(Surface a, Surface b, Point3D p)
    {
        var c = Gradient(a, p).Cross(Gradient(b, p));
        return c.Length < 1e-7 ? null : c / c.Length;
    }

    static Vector3D Gradient(Surface s, Point3D p)
    {
        var uv = Uv.Of(s, p);
        return s.Normal(uv.X, uv.Y);
    }

    /// <summary>
    /// A point near both surfaces moved onto both (Newton's method on their signed distances, each step the shortest
    /// that zeroes their linear parts); null where it does not settle.
    /// </summary>
    public static Point3D? Project(Surface a, Surface b, Point3D p, double size)
    {
        var start = p;
        for (var k = 0; k < 40; k++)
        {
            var (fa, fb) = (Uv.Side(a, p), Uv.Side(b, p));
            if (Math.Abs(fa) <= 1e-15 * (1 + size) && Math.Abs(fb) <= 1e-15 * (1 + size)) return p;
            var (ga, gb) = (Gradient(a, p), Gradient(b, p));
            var (aa, ab, bb) = (ga.Dot(ga), ga.Dot(gb), gb.Dot(gb));
            var det = aa * bb - ab * ab;
            if (det < 1e-14) return null;
            var (x, y) = ((-fa * bb + fb * ab) / det, (-fb * aa + fa * ab) / det);
            var step = ga * x + gb * y;
            p += step;
            if ((p - start).Length > size) return null;
            if (step.Length <= 1e-16 * (1 + size)) return p;
        }
        var (ea, eb) = (Math.Abs(Uv.Side(a, p)), Math.Abs(Uv.Side(b, p)));
        return ea <= 1e-12 * (1 + size) && eb <= 1e-12 * (1 + size) ? p : null;
    }

    /// <summary>
    /// Where a traced curve meets another curve: where that curve crosses the traced curve's surface it does not lie on
    /// (or, lying on neither, the first), kept where it is on the traced curve. The same branch of the same surfaces
    /// overlaps.
    /// </summary>
    public static (List<Point3D> Points, bool Overlap) Meet(Trace3 c, Curve3 d)
    {
        if (d is Pole pole) return (c.Nearest(pole.Point).Distance <= Intersections.Snap ? [pole.Point] : [], false);
        var (onA, onB) = (Lies(d, c.A, c), Lies(d, c.B, c));
        if (onA && onB)
        {
            var probe = d is Trace3 e ? e.At(e.End / 3) : d.At(0);
            return ([], c.Nearest(probe).Distance <= Intersections.Snap);
        }
        var crossings = Crossings(d, onA ? c.B : c.A, c);
        var points = new List<Point3D>();
        foreach (var p in crossings)
            if (c.Nearest(p).Distance <= Intersections.Snap && points.All(q => (q - p).Length > Intersections.Snap)) points.Add(p);
        return (points, false);
    }

    // Whether a curve lies on a surface: a few of its points near the traced curve, on it.
    static bool Lies(Curve3 d, Surface s, Trace3 near)
    {
        if (d is Trace3 t && (Intersections.SameSurface(t.A, s) || Intersections.SameSurface(t.B, s))) return true;
        return Probes(d, near).All(p => Uv.Distance(s, p) <= Intersections.Snap);
    }

    static IEnumerable<Point3D> Probes(Curve3 d, Trace3 near) => d switch
    {
        Line3 l => new[] { -1.0, 0, 1 }.Select(k => l.At((near.Points[0] - l.Origin).Dot(l.Direction) + k * Size(near))),
        Trace3 t => Enumerable.Range(0, 5).Select(i => t.Points[i * (t.Points.Length - 1) / 4]),
        _ when d.Period is not null => Enumerable.Range(0, 6).Select(i => d.At(2 * Math.PI * i / 6 + 0.1)),
        _ => new[] { -1.0, 0, 1 }.Select(d.At),
    };

    static double Size(Trace3 c)
    {
        var hull = c.Hull(c.Knots[0], c.End).ToList();
        var lo = new Point3D(hull.Min(p => p.X), hull.Min(p => p.Y), hull.Min(p => p.Z));
        var hi = new Point3D(hull.Max(p => p.X), hull.Max(p => p.Y), hull.Max(p => p.Z));
        return (hi - lo).Length;
    }

    // Where a curve crosses a surface (its signed distance changes sign; a touch is not found), near the traced curve.
    static IEnumerable<Point3D> Crossings(Curve3 d, Surface s, Trace3 near)
    {
        if (d is Line3 l)
        {
            foreach (var t in Intersections.Hits(l.Origin, l.Direction, s)) yield return l.At(t);
            yield break;
        }
        List<double> ts;
        switch (d)
        {
            case Trace3 t:
                ts = [.. t.Spans(t.Knots[0], t.End).SelectMany(x => Enumerable.Range(0, 4).Select(k => x.From + (x.To - x.From) * k / 4)), t.End];
                break;
            case Hyperbola3 h:
            {
                // As far along the branch as the traced curve reaches from its centre, twice over.
                var far = near.Points.Max(p => (p - h.Frame.Origin).Length) * 2;
                var top = Math.Asinh(far / Math.Min(h.Major, h.Minor));
                ts = [.. Enumerable.Range(0, 1441).Select(i => -top + 2 * top * i / 1440)];
                break;
            }
            default:
                ts = [.. Enumerable.Range(0, 721).Select(i => 2 * Math.PI * i / 720)];
                break;
        }
        var values = ts.Select(t => Uv.Side(s, d.At(t))).ToArray();
        for (var i = 0; i + 1 < ts.Count; i++)
        {
            if (values[i] == 0) yield return d.At(ts[i]);
            if (values[i] * values[i + 1] >= 0) continue;
            var (lo, hi) = (ts[i], ts[i + 1]);
            for (var k = 0; k < 80; k++)
            {
                var mid = (lo + hi) / 2;
                if (Uv.Side(s, d.At(mid)) * values[i] > 0) lo = mid; else hi = mid;
            }
            yield return d.At((lo + hi) / 2);
        }
    }
}
