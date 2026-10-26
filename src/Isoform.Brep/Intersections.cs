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
        if (Math.Abs(na) > Parallel) return SurfaceMeeting.Refused("a plane at an angle to a cylinder's axis, which meets it in an ellipse");
        var s = n.Dot(c.Frame.Origin - p.Frame.Origin);
        if (Math.Abs(s) > c.Radius + Snap) return SurfaceMeeting.Nothing;
        if (Math.Abs(s) > c.Radius - Snap) return SurfaceMeeting.Refused("a plane tangent to a cylinder");
        var w = Math.Sqrt(c.Radius * c.Radius - s * s);
        var foot = c.Frame.Origin - n * s;
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
        _ => throw new NotSupportedException($"{a.GetType().Name} against {b.GetType().Name}"),
    };

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
        _ => throw new NotSupportedException(c.GetType().Name),
    };

    /// <summary>
    /// The parameter of <paramref name="p"/> on the piece of <paramref name="c"/> over [t0, t1] (t0 &lt; t1, a circle's
    /// taken round from t0), or null when the point is not within <see cref="Snap"/> of the piece.
    /// </summary>
    public static double? On(Curve3 c, double t0, double t1, Point3D p)
    {
        var t = Param(c, p);
        if (c is Circle3)
        {
            t = t0 + Wrap(t - t0);
            // A point just before t0 wraps to near t0 + 2π.
            if (t > t1 && (p - c.At(t0)).Length <= Snap) t = t0;
        }
        var slack = c is Circle3 k ? Snap / k.Radius : Snap;
        if (t < t0 - slack || t > t1 + slack) return null;
        t = Math.Clamp(t, t0, t1);
        return (c.At(t) - p).Length <= Snap ? t : null;
    }

    /// <summary>The distance from a point to the piece of a curve over [t0, t1] (t0 &lt; t1).</summary>
    public static double Distance(Curve3 c, double t0, double t1, Point3D p)
    {
        var t = Param(c, p);
        if (c is Circle3) t = t0 + Wrap(t - t0);
        if (t >= t0 && t <= t1) return (c.At(t) - p).Length;
        return Math.Min((c.At(t0) - p).Length, (c.At(t1) - p).Length);
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
            default:
                throw new NotSupportedException($"a {c.GetType().Name} on a {s.GetType().Name}");
        }
    }

    /// <summary>The p-curve moved by <paramref name="du"/> in u.</summary>
    public static Curve2 Shift(Curve2 pc, double du) => du == 0 ? pc : pc switch
    {
        Line2 l => l with { Origin = new Point2D(l.Origin.X + du, l.Origin.Y) },
        Circle2 c => c with { Center = new Point2D(c.Center.X + du, c.Center.Y) },
        _ => throw new NotSupportedException(pc.GetType().Name),
    };

    /// <summary>The p-curve as a function of s with t = α·s + β (α = ±1).</summary>
    public static Curve2 Reparametrised(Curve2 pc, double alpha, double beta) => pc switch
    {
        Line2 l => new Line2(l.At(beta), l.Direction * alpha),
        Circle2 c => new Circle2(c.Center, c.Radius, c.Phase + c.Sense * beta, c.Sense * (int)Math.Round(alpha)),
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
            default:
                throw new NotSupportedException($"a {pc.GetType().Name} on a {s.GetType().Name}");
        }
    }
}
