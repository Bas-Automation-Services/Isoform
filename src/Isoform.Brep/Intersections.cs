using BasAutomation.Geometry;

namespace Isoform.Brep;

/// <summary>How two surfaces meet: not at all, along curves, everywhere (the same surface), or in a way not supported yet.</summary>
internal enum Meeting { None, Curves, Coincident, Unsupported }

internal sealed record SurfaceMeeting(Meeting Kind, IReadOnlyList<Curve3> Curves, string? Why = null)
{
    public static readonly SurfaceMeeting Nothing = new(Meeting.None, []);
    public static readonly SurfaceMeeting Same = new(Meeting.Coincident, []);
    public static SurfaceMeeting Along(params Curve3[] curves) => new(Meeting.Curves, curves);
    public static SurfaceMeeting Refused(string why) => new(Meeting.Unsupported, [], why);
}

/// <summary>
/// Closed-form intersections of the kernel's carriers. Points closer than <see cref="Snap"/> are the same point;
/// directions whose cross product is shorter than <see cref="Parallel"/> are parallel.
/// </summary>
internal static class Intersections
{
    /// <summary>The distance below which two computed points are one (well above rounding, well below any feature).</summary>
    public const double Snap = 1e-6;

    const double Parallel = 1e-12;

    public static SurfaceMeeting Of(Surface a, Surface b) => (a, b) switch
    {
        (Plane p, Plane q) => PlanePlane(p, q),
        (Plane p, Cylinder c) => PlaneCylinder(p, c),
        (Cylinder c, Plane p) => PlaneCylinder(p, c),
        (Cylinder c, Cylinder d) => CylinderCylinder(c, d),
        _ => SurfaceMeeting.Refused($"a {a.GetType().Name.ToLowerInvariant()} against a {b.GetType().Name.ToLowerInvariant()}"),
    };

    static SurfaceMeeting PlanePlane(Plane p, Plane q)
    {
        var (n1, n2) = (p.Frame.ZAxis, q.Frame.ZAxis);
        var u = n1.Cross(n2);
        if (u.Length < Parallel)
            return Math.Abs((q.Frame.Origin - p.Frame.Origin).Dot(n1)) <= Snap ? SurfaceMeeting.Same : SurfaceMeeting.Nothing;
        var line = PlanesLine(p.Frame.Origin, n1, q.Frame.Origin, n2);
        return SurfaceMeeting.Along(line);
    }

    // The line two non-parallel planes share, its origin the point nearest the first plane's origin.
    static Line3 PlanesLine(Point3D o1, Vector3D n1, Point3D o2, Vector3D n2)
    {
        var u = n1.Cross(n2);
        var (h1, h2) = (n1.Dot(o1.AsVector()), n2.Dot(o2.AsVector()));
        var p = (n2.Cross(u) * h1 + u.Cross(n1) * h2) / u.Dot(u);
        var d = u.Normalized();
        var point = new Point3D(p.X, p.Y, p.Z);
        return new Line3(point + d * (o1 - point).Dot(d), d);
    }

    static SurfaceMeeting PlaneCylinder(Plane p, Cylinder c)
    {
        var (n, a) = (p.Frame.ZAxis, c.Frame.ZAxis);
        var na = n.Dot(a);
        if (Math.Abs(na) >= 1 - Parallel)
        {
            var center = c.Frame.Origin + a * (n.Dot(p.Frame.Origin - c.Frame.Origin) / na);
            return SurfaceMeeting.Along(new Circle3(RigidTransform3D.FromFrame(center, a, c.Frame.XAxis), c.Radius));
        }
        if (Math.Abs(na) > Parallel)
        {
            // Centre on the axis; minor axis R across it, major axis R / |n·a| down the slope.
            var center = c.Frame.Origin + a * (n.Dot(p.Frame.Origin - c.Frame.Origin) / na);
            var minor = a.Cross(n).Normalized();
            var major = n.Cross(minor);
            return SurfaceMeeting.Along(new Ellipse3(RigidTransform3D.FromFrame(center, n, major), c.Radius / Math.Abs(na), c.Radius));
        }
        var s = n.Dot(c.Frame.Origin - p.Frame.Origin);
        if (Math.Abs(s) > c.Radius + Snap) return SurfaceMeeting.Nothing;
        var foot = c.Frame.Origin - n * s;
        // Tangent (a fillet's cylinder on its faces): the one line they touch along.
        if (Math.Abs(s) > c.Radius - Snap) return SurfaceMeeting.Along(new Line3(foot, a));
        var w = Math.Sqrt(c.Radius * c.Radius - s * s);
        var e = a.Cross(n);
        return SurfaceMeeting.Along(new Line3(foot + e * w, a), new Line3(foot - e * w, a));
    }

    static SurfaceMeeting CylinderCylinder(Cylinder c, Cylinder d)
    {
        var a = c.Frame.ZAxis;
        if (a.Cross(d.Frame.ZAxis).Length >= Parallel) return SurfaceMeeting.Refused("two cylinders whose axes are not parallel");
        var w = d.Frame.Origin - c.Frame.Origin;
        var perp = w - a * w.Dot(a);
        var dist = perp.Length;
        var (r1, r2) = (c.Radius, d.Radius);
        if (dist <= Snap) return Math.Abs(r1 - r2) <= Snap ? SurfaceMeeting.Same : SurfaceMeeting.Nothing;
        if (dist > r1 + r2 + Snap || dist < Math.Abs(r1 - r2) - Snap) return SurfaceMeeting.Nothing;
        if (Math.Abs(dist - (r1 + r2)) <= Snap || Math.Abs(dist - Math.Abs(r1 - r2)) <= Snap)
            return SurfaceMeeting.Refused("two cylinders that touch along a line");
        var x = (r1 * r1 - r2 * r2 + dist * dist) / (2 * dist);
        var h = Math.Sqrt(r1 * r1 - x * x);
        var ex = perp / dist;
        var ey = a.Cross(ex);
        var mid = c.Frame.Origin + ex * x;
        return SurfaceMeeting.Along(new Line3(mid + ey * h, a), new Line3(mid - ey * h, a));
    }

    /// <summary>Where two curves meet, or that they are the same curve (<c>Overlap</c>, no points).</summary>
    public static (List<Point3D> Points, bool Overlap) Meet(Curve3 a, Curve3 b) => (a, b) switch
    {
        (Line3 p, Line3 q) => LineLine(p, q),
        (Line3 p, Circle3 c) => (LineCircle(p, c), false),
        (Circle3 c, Line3 p) => (LineCircle(p, c), false),
        (Circle3 c, Circle3 d) => CircleCircle(c, d),
        (Line3 p, Ellipse3 e) => (LineConic(p, e), false),
        (Ellipse3 e, Line3 p) => (LineConic(p, e), false),
        (Ellipse3 or Circle3, Ellipse3 or Circle3) => ConicConic(a, b),
        _ => throw new NotSupportedException($"{a.GetType().Name} against {b.GetType().Name}"),
    };

    // A circle or an ellipse: its frame and semi-axes (along its X and Y).
    static (RigidTransform3D Frame, double A, double B) Conic(Curve3 c) => c switch
    {
        Circle3 k => (k.Frame, k.Radius, k.Radius),
        Ellipse3 e => (e.Frame, e.Major, e.Minor),
        _ => throw new NotSupportedException(c.GetType().Name),
    };

    static bool OnConic(Curve3 c, Point3D p) => (c.At(Param(c, p)) - p).Length <= Snap;

    static List<Point3D> LineConic(Line3 line, Curve3 conic)
    {
        var (f, a, b) = Conic(conic);
        var z = f.ZAxis;
        var dz = line.Direction.Dot(z);
        if (Math.Abs(dz) > Parallel)
        {
            var p = line.At((f.Origin - line.Origin).Dot(z) / dz);
            return OnConic(conic, p) ? [p] : [];
        }
        if (Math.Abs((line.Origin - f.Origin).Dot(z)) > Snap) return [];
        // In the conic's frame: (x/a)² + (y/b)² = 1 along the line.
        var (o, d) = (f.ApplyInverse(line.Origin), f.ApplyInverse(line.Direction));
        var qa = d.X * d.X / (a * a) + d.Y * d.Y / (b * b);
        var qb = 2 * (o.X * d.X / (a * a) + o.Y * d.Y / (b * b));
        var qc = o.X * o.X / (a * a) + o.Y * o.Y / (b * b) - 1;
        var disc = qb * qb - 4 * qa * qc;
        var scale = 4 * qa * Snap / Math.Min(a, b);
        if (disc < -scale) return [];
        if (disc <= scale) return [line.At(-qb / (2 * qa))];
        var root = Math.Sqrt(disc);
        return [line.At((-qb - root) / (2 * qa)), line.At((-qb + root) / (2 * qa))];
    }

    static (List<Point3D>, bool) ConicConic(Curve3 c, Curve3 d)
    {
        var (f, _, _) = Conic(c);
        var (g, a, b) = Conic(d);
        if (f.ZAxis.Cross(g.ZAxis).Length >= Parallel)
        {
            // Planes at an angle: the line they share against one, kept where it is on the other.
            var line = PlanesLine(f.Origin, f.ZAxis, g.Origin, g.ZAxis);
            return ([.. LineConic(line, c).Where(p => OnConic(d, p))], false);
        }
        if (Math.Abs((g.Origin - f.Origin).Dot(f.ZAxis)) > Snap) return ([], false);
        // One plane: the same conic, or where d's equation changes sign along c (tangency is not found).
        double Implicit(Point3D p)
        {
            var q = g.ApplyInverse(p);
            return q.X * q.X / (a * a) + q.Y * q.Y / (b * b) - 1;
        }
        const int n = 720;
        var values = Enumerable.Range(0, n + 1).Select(i => Implicit(c.At(2 * Math.PI * i / n))).ToArray();
        if (values.All(v => Math.Abs(v) <= Snap / Math.Min(a, b))) return ([], true);
        var points = new List<Point3D>();
        for (var i = 0; i < n; i++)
        {
            if (values[i] * values[i + 1] > 0) continue;
            var (lo, hi) = (2 * Math.PI * i / n, 2 * Math.PI * (i + 1) / n);
            for (var k = 0; k < 80; k++)
            {
                var mid = (lo + hi) / 2;
                if (Implicit(c.At(mid)) * values[i] > 0) lo = mid; else hi = mid;
            }
            var p = c.At((lo + hi) / 2);
            if (points.All(q => (q - p).Length > Snap)) points.Add(p);
        }
        return (points, false);
    }

    static (List<Point3D>, bool) LineLine(Line3 p, Line3 q)
    {
        var (d1, d2) = (p.Direction, q.Direction);
        var w0 = p.Origin - q.Origin;
        if (d1.Cross(d2).Length < Parallel)
            return ([], (w0 - d1 * w0.Dot(d1)).Length <= Snap);
        var b = d1.Dot(d2);
        var (d, e) = (d1.Dot(w0), d2.Dot(w0));
        var denominator = 1 - b * b;
        var (s, t) = ((b * e - d) / denominator, (e - b * d) / denominator);
        var (x, y) = (p.At(s), q.At(t));
        return (x - y).Length <= Snap ? ([Mid(x, y)], false) : ([], false);
    }

    static List<Point3D> LineCircle(Line3 line, Circle3 circle)
    {
        var (c, z, r) = (circle.Frame.Origin, circle.Frame.ZAxis, circle.Radius);
        var dz = line.Direction.Dot(z);
        if (Math.Abs(dz) > Parallel)
        {
            var p = line.At((c - line.Origin).Dot(z) / dz);
            return Math.Abs((p - c).Length - r) <= Snap ? [p] : [];
        }
        if (Math.Abs((line.Origin - c).Dot(z)) > Snap) return [];
        var f = line.Origin - c;
        var (bq, cq) = (2 * f.Dot(line.Direction), f.Dot(f) - r * r);
        var disc = bq * bq - 4 * cq;
        if (disc < -4 * r * Snap) return [];
        if (disc <= 4 * r * Snap) return [line.At(-bq / 2)];
        var root = Math.Sqrt(disc);
        return [line.At((-bq - root) / 2), line.At((-bq + root) / 2)];
    }

    static (List<Point3D>, bool) CircleCircle(Circle3 a, Circle3 b)
    {
        var (z1, z2) = (a.Frame.ZAxis, b.Frame.ZAxis);
        if (z1.Cross(z2).Length < Parallel)
        {
            var offset = b.Frame.Origin - a.Frame.Origin;
            if (Math.Abs(offset.Dot(z1)) > Snap) return ([], false);
            var dist = offset.Length;
            if (dist <= Snap) return ([], Math.Abs(a.Radius - b.Radius) <= Snap);
            if (dist > a.Radius + b.Radius + Snap || dist < Math.Abs(a.Radius - b.Radius) - Snap) return ([], false);
            var x = (a.Radius * a.Radius - b.Radius * b.Radius + dist * dist) / (2 * dist);
            var h = Math.Sqrt(Math.Max(0, a.Radius * a.Radius - x * x));
            var ex = offset / dist;
            var ey = z1.Cross(ex);
            var mid = a.Frame.Origin + ex * x;
            return h <= Snap ? ([mid], false) : ([mid + ey * h, mid - ey * h], false);
        }
        // Planes at an angle: the line they share against one circle, kept where it is on the other.
        var line = PlanesLine(a.Frame.Origin, z1, b.Frame.Origin, z2);
        return ([.. LineCircle(line, a).Where(p => Math.Abs((p - b.Frame.Origin).Length - b.Radius) <= Snap)], false);
    }

    /// <summary>The curve's parameter at a point on it (a circle's in (−π, π]).</summary>
    public static double Param(Curve3 c, Point3D p) => c switch
    {
        Line3 l => (p - l.Origin).Dot(l.Direction),
        Circle3 k => Math.Atan2((p - k.Frame.Origin).Dot(k.Frame.YAxis), (p - k.Frame.Origin).Dot(k.Frame.XAxis)),
        Ellipse3 e => Math.Atan2((p - e.Frame.Origin).Dot(e.Frame.YAxis) / e.Minor, (p - e.Frame.Origin).Dot(e.Frame.XAxis) / e.Major),
        _ => throw new NotSupportedException(c.GetType().Name),
    };

    /// <summary>
    /// The parameter of <paramref name="p"/> on the piece of <paramref name="c"/> over [t0, t1] (t0 &lt; t1, a circle's
    /// taken round from t0), or null when the point is not within <see cref="Snap"/> of the piece.
    /// </summary>
    public static double? On(Curve3 c, double t0, double t1, Point3D p)
    {
        var t = Param(c, p);
        if (c.Period is not null)
        {
            t = t0 + Wrap(t - t0);
            // A point just before t0 wraps to near t0 + 2π.
            if (t > t1 && (p - c.At(t0)).Length <= Snap) t = t0;
        }
        var slack = c switch { Circle3 k => Snap / k.Radius, Ellipse3 e => Snap / e.Minor, _ => Snap };
        if (t < t0 - slack || t > t1 + slack) return null;
        t = Math.Clamp(t, t0, t1);
        return (c.At(t) - p).Length <= Snap ? t : null;
    }

    /// <summary>The distance from a point to the piece of a curve over [t0, t1] (t0 &lt; t1).</summary>
    public static double Distance(Curve3 c, double t0, double t1, Point3D p)
    {
        if (c is Ellipse3) return Nearest(c, t0, t1, p);
        var t = Param(c, p);
        if (c.Period is not null) t = t0 + Wrap(t - t0);
        if (t >= t0 && t <= t1) return (c.At(t) - p).Length;
        return Math.Min((c.At(t0) - p).Length, (c.At(t1) - p).Length);
    }

    // The distance to the nearest point of a piece: golden-section search on each of 32 sub-pieces.
    static double Nearest(Curve3 c, double t0, double t1, Point3D p)
    {
        var best = Math.Min((c.At(t0) - p).Length, (c.At(t1) - p).Length);
        const double g = 0.6180339887498949;
        for (var i = 0; i < 32; i++)
        {
            var (a, b) = (t0 + (t1 - t0) * i / 32, t0 + (t1 - t0) * (i + 1) / 32);
            double F(double t) => (c.At(t) - p).Length;
            var (x1, x2) = (b - g * (b - a), a + g * (b - a));
            var (f1, f2) = (F(x1), F(x2));
            for (var k = 0; k < 80; k++)
            {
                if (f1 < f2)
                {
                    (b, x2, f2) = (x2, x1, f1);
                    x1 = b - g * (b - a);
                    f1 = F(x1);
                }
                else
                {
                    (a, x1, f1) = (x1, x2, f2);
                    x2 = a + g * (b - a);
                    f2 = F(x2);
                }
            }
            best = Math.Min(best, Math.Min(f1, f2));
        }
        return best;
    }

    /// <summary>An angle taken into [0, 2π).</summary>
    public static double Wrap(double angle)
    {
        var a = angle % (2 * Math.PI);
        return a < 0 ? a + 2 * Math.PI : a;
    }

    /// <summary>Where the ray p + t·d (t &gt; 0) crosses a surface: the parameters t.</summary>
    public static IEnumerable<double> Hits(Point3D p, Vector3D d, Surface s)
    {
        switch (s)
        {
            case Plane plane:
                var dn = d.Dot(plane.Frame.ZAxis);
                if (Math.Abs(dn) > Parallel) yield return (plane.Frame.Origin - p).Dot(plane.Frame.ZAxis) / dn;
                break;
            case Cylinder c:
                var z = c.Frame.ZAxis;
                var w = p - c.Frame.Origin;
                var (a, b) = (d - z * d.Dot(z), w - z * w.Dot(z));
                var (qa, qb, qc) = (a.Dot(a), 2 * a.Dot(b), b.Dot(b) - c.Radius * c.Radius);
                if (qa < Parallel) yield break;
                var disc = qb * qb - 4 * qa * qc;
                if (disc < 0) yield break;
                var root = Math.Sqrt(disc);
                yield return (-qb - root) / (2 * qa);
                yield return (-qb + root) / (2 * qa);
                break;
            default:
                throw new NotSupportedException(s.GetType().Name);
        }
    }

    /// <summary>Whether two surfaces are the same surface (an equal plane or cylinder), whichever way their normals point.</summary>
    public static bool SameSurface(Surface a, Surface b) => Of(a, b).Kind == Meeting.Coincident;

    static Point3D Mid(Point3D a, Point3D b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
}

/// <summary>
/// Surface parameters and the flat (u, v) the boolean works in: (u, v) itself on a plane, (R·u, v) on a cylinder,
/// so that lengths and angles in it are the surface's own.
/// </summary>
internal static class Uv
{
    /// <summary>A point's (u, v); a cylinder's u in (−π, π].</summary>
    public static Point2D Of(Surface s, Point3D p) => s switch
    {
        Plane plane => new((p - plane.Frame.Origin).Dot(plane.Frame.XAxis), (p - plane.Frame.Origin).Dot(plane.Frame.YAxis)),
        Cylinder c => new(Math.Atan2((p - c.Frame.Origin).Dot(c.Frame.YAxis), (p - c.Frame.Origin).Dot(c.Frame.XAxis)),
            (p - c.Frame.Origin).Dot(c.Frame.ZAxis)),
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    public static double Scale(Surface s) => s is Cylinder c ? c.Radius : 1;

    /// <summary>The distance from a point to the surface.</summary>
    public static double Distance(Surface s, Point3D p) => s switch
    {
        Plane plane => Math.Abs((p - plane.Frame.Origin).Dot(plane.Frame.ZAxis)),
        Cylinder c => Math.Abs(Radial(c, p) - c.Radius),
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    static double Radial(Cylinder c, Point3D p)
    {
        var w = p - c.Frame.Origin;
        return (w - c.Frame.ZAxis * w.Dot(c.Frame.ZAxis)).Length;
    }

    /// <summary>
    /// The p-curve of a curve on the surface, with the curve's own parameter: <c>s.At(pc.At(t)) = c.At(t)</c>. On a
    /// cylinder u is not yet placed in any period (see <see cref="Shift"/>).
    /// </summary>
    public static Curve2 Pcurve(Surface s, Curve3 c)
    {
        switch (s, c)
        {
            case (Plane p, Line3 l):
                return new Line2(Of(p, l.Origin), new Vector2D(l.Direction.Dot(p.Frame.XAxis), l.Direction.Dot(p.Frame.YAxis)));
            case (Plane p, Circle3 k):
            {
                var x = k.Frame.XAxis;
                var phase = Math.Atan2(x.Dot(p.Frame.YAxis), x.Dot(p.Frame.XAxis));
                return new Circle2(Of(p, k.Frame.Origin), k.Radius, phase, k.Frame.ZAxis.Dot(p.Frame.ZAxis) > 0 ? 1 : -1);
            }
            case (Cylinder cyl, Line3 l):
                return new Line2(Of(cyl, l.Origin), new Vector2D(0, l.Direction.Dot(cyl.Frame.ZAxis)));
            case (Cylinder cyl, Circle3 k):
            {
                var x = k.Frame.XAxis;
                var phase = Math.Atan2(x.Dot(cyl.Frame.YAxis), x.Dot(cyl.Frame.XAxis));
                var sense = k.Frame.ZAxis.Dot(cyl.Frame.ZAxis) > 0 ? 1 : -1;
                return new Line2(new Point2D(phase, (k.Frame.Origin - cyl.Frame.Origin).Dot(cyl.Frame.ZAxis)), new Vector2D(sense, 0));
            }
            case (Plane p, Ellipse3 e):
            {
                var x = e.Frame.XAxis;
                var rotation = Math.Atan2(x.Dot(p.Frame.YAxis), x.Dot(p.Frame.XAxis));
                return new Ellipse2(Of(p, e.Frame.Origin), e.Major, e.Minor, rotation, e.Frame.ZAxis.Dot(p.Frame.ZAxis) > 0 ? 1 : -1, 0);
            }
            case (Cylinder cyl, Ellipse3 e):
            {
                // A plane section: across the axis it is the cylinder's circle, so u runs with t; along it a cosine.
                var z = cyl.Frame.ZAxis;
                var (xe, ye) = (e.Frame.XAxis, e.Frame.YAxis);
                var (xp, yp) = ((xe - z * xe.Dot(z)) * e.Major, (ye - z * ye.Dot(z)) * e.Minor);
                if (Math.Abs(xp.Length - cyl.Radius) > Intersections.Snap || Math.Abs(yp.Length - cyl.Radius) > Intersections.Snap || Math.Abs(xp.Dot(yp)) > Intersections.Snap)
                    throw new NotSupportedException("an ellipse on a cylinder that is not a plane section of it");
                var u0 = Math.Atan2(xp.Dot(cyl.Frame.YAxis), xp.Dot(cyl.Frame.XAxis));
                var slope = xp.Cross(yp).Dot(z) > 0 ? 1 : -1;
                var (ca, cb) = (e.Major * xe.Dot(z), e.Minor * ye.Dot(z));
                return new Wave2(u0, (e.Frame.Origin - cyl.Frame.Origin).Dot(z), slope, Math.Sqrt(ca * ca + cb * cb), Math.Atan2(-cb, ca));
            }
            default:
                throw new NotSupportedException($"a {c.GetType().Name} on a {s.GetType().Name}");
        }
    }

    /// <summary>The p-curve moved by <paramref name="du"/> in u.</summary>
    public static Curve2 Shift(Curve2 pc, double du) => du == 0 ? pc : pc switch
    {
        Line2 l => l with { Origin = new Point2D(l.Origin.X + du, l.Origin.Y) },
        Circle2 c => c with { Center = new Point2D(c.Center.X + du, c.Center.Y) },
        Ellipse2 e => e with { Center = new Point2D(e.Center.X + du, e.Center.Y) },
        Wave2 w => w with { U0 = w.U0 + du },
        _ => throw new NotSupportedException(pc.GetType().Name),
    };

    /// <summary>The p-curve as a function of s with t = α·s + β (α = ±1).</summary>
    public static Curve2 Reparametrised(Curve2 pc, double alpha, double beta) => pc switch
    {
        Line2 l => new Line2(l.At(beta), l.Direction * alpha),
        Circle2 c => new Circle2(c.Center, c.Radius, c.Phase + c.Sense * beta, c.Sense * (int)Math.Round(alpha)),
        Ellipse2 e => e with { Offset = alpha * (beta + e.Offset), Sense = e.Sense * (int)Math.Round(alpha) },
        Wave2 w => w with { U0 = w.U0 + w.Slope * beta, Slope = w.Slope * (int)Math.Round(alpha), Phase = alpha * (beta + w.Phase) },
        _ => throw new NotSupportedException(pc.GetType().Name),
    };

    /// <summary>The p-curve over [from, to] (in that order) as a segment in flat (u, v).</summary>
    public static Segment Flat(Surface s, Curve2 pc, double from, double to)
    {
        var k = Scale(s);
        switch (pc)
        {
            case Line2 l:
                Point2D F(Point2D p) => new(p.X * k, p.Y);
                return new LineSegment(F(l.At(from)), F(l.At(to)));
            case Circle2 c when k == 1:
                return new ArcSegment(c.Center, c.Radius, c.Phase + c.Sense * from, c.Sense * (to - from));
            case Ellipse2 when k == 1:
            case Wave2:
                return new CurveSegment(pc, from, to, k);
            default:
                throw new NotSupportedException($"a {pc.GetType().Name} on a {s.GetType().Name}");
        }
    }
}
