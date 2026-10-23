using BasAutomation.Geometry;

namespace Isoform.Geometry;

/// <summary>
/// Exact intersection curves of analytic surfaces, and exact curve–surface points. Two families cover planes,
/// cylinders, cones, spheres and tori in closed form:
/// <list type="bullet">
/// <item>surfaces of revolution round one axis meet in circles round it: a 2D problem in the (ρ, z) half-plane, where
/// each surface is a line or a circle;</item>
/// <item>surfaces swept along one direction (planes, parallel cylinders) meet in lines along it: a 2D problem in the
/// cross-section, again lines and circles.</item>
/// </list>
/// A plane cutting a cylinder obliquely gives an ellipse. Every other pair has no closed form and is unsupported.
/// </summary>
public static class SurfaceIntersection
{
    const double Parallel = 1e-9;
    const double OnAxis = 1e-9;

    // Tangent contact: a discriminant this small (relative to r²) is a double root. Two real roots this close would be
    // far below anything the mesh resolves, and taking the root of round-off would move a tangent edge by ~1e-7.
    const double Tangent = 1e-12;

    /// <summary>The intersection curve of two surfaces through the branch nearest to <paramref name="seed"/>; null if none or unsupported.</summary>
    public static Curve? Curve(Surface a, Surface b, Point3D seed)
    {
        if (CommonAxis(a, b) is { } axis) return Revolved(a, b, axis.Point, axis.Direction, seed);
        if (CommonDirection(a, b) is { } d) return Swept(a, b, d, seed);
        if (a.Kind == SurfaceKind.Cylinder && b.Kind == SurfaceKind.Plane) (a, b) = (b, a);
        if (a.Kind == SurfaceKind.Plane && b.Kind == SurfaceKind.Cylinder) return Oblique(a, b);
        if (a.Kind == SurfaceKind.Cylinder && b.Kind == SurfaceKind.Cylinder) return Mitre(a, b, seed);
        if (a.Kind == SurfaceKind.Torus && b.Kind == SurfaceKind.Cylinder) (a, b) = (b, a);
        if (a.Kind == SurfaceKind.Cylinder && b.Kind == SurfaceKind.Torus) return TubeCircle(a, b);
        return null;
    }

    /// <summary>
    /// A cylinder of the torus's tube radius whose axis touches the torus's centre circle (a straight fillet running on
    /// into a rolled one): they touch along the tube circle there. Null for any other cylinder.
    /// </summary>
    static Circle? TubeCircle(Surface cylinder, Surface torus)
    {
        var w = cylinder.Origin - torus.Origin;
        if (Math.Abs(cylinder.Radius - torus.Minor) > OnAxis || Math.Abs(cylinder.Axis.Dot(torus.Axis)) > Parallel || Math.Abs(torus.Axis.Dot(w)) > OnAxis) return null;
        var foot = cylinder.Origin - cylinder.Axis * cylinder.Axis.Dot(w);
        return Math.Abs((foot - torus.Origin).Length - torus.Radius) > OnAxis ? null : new Circle(foot, cylinder.Axis, torus.Axis, torus.Minor);
    }

    /// <summary>
    /// Equal cylinders whose axes cross (two fillets meeting at a corner) meet in two ellipses, one in each plane
    /// bisecting the axes; the one nearer the seed.
    /// </summary>
    static Ellipse? Mitre(Surface a, Surface b, Point3D seed)
    {
        if (Math.Abs(a.Radius - b.Radius) > OnAxis) return null;
        var normal = a.Axis.Cross(b.Axis);
        if (normal.Length <= Parallel) return null;
        normal = normal.Normalized();
        if (Math.Abs(normal.Dot(b.Origin - a.Origin)) > OnAxis) return null; // skew axes
        // Where the axes cross: a.Origin + a.Axis·s, with s from the plane through b's axis and the common normal.
        var across = b.Axis.Cross(normal);
        var crossing = a.Origin + a.Axis * (across.Dot(b.Origin - a.Origin) / across.Dot(a.Axis));
        var bisector = new[] { (a.Axis + b.Axis).Normalized(), (a.Axis - b.Axis).Normalized() }
            .MinBy(n => Math.Abs(n.Dot(seed - crossing)));
        return Oblique(new Surface(0, "", SurfaceKind.Plane, crossing, bisector), a);
    }

    /// <summary>
    /// Where <paramref name="curve"/> crosses <paramref name="surface"/> near <paramref name="seed"/> (Newton on the
    /// curve parameter), and how transversally: |∇s · unit tangent|, 0 for a tangent touch.
    /// </summary>
    public static (Point3D Point, double Transversality)? Cross(Curve curve, Surface surface, Point3D seed)
    {
        var t = curve.Parameter(seed);
        for (var it = 0; it < 60; it++)
        {
            var p = curve.At(t);
            var slope = surface.Gradient(p).Dot(curve.Tangent(t));
            if (Math.Abs(slope) < 1e-300) return null;
            var step = surface.Distance(p) / slope;
            t -= step;
            if (Math.Abs(step) <= 1e-15 * (1 + Math.Abs(t))) break;
        }
        var q = curve.At(t);
        if (Math.Abs(surface.Distance(q)) > 1e-9) return null;
        var tangent = curve.Tangent(t).Normalized();
        return (q, Math.Abs(surface.Gradient(q).Dot(tangent)));
    }

    /// <summary>A unit vector perpendicular to <paramref name="axis"/> that does not depend on the axis' sign.</summary>
    public static Vector3D Perpendicular(Vector3D axis)
    {
        var a = axis.Normalized();
        var big = Math.Abs(a.X) >= Math.Abs(a.Y) && Math.Abs(a.X) >= Math.Abs(a.Z) ? a.X : Math.Abs(a.Y) >= Math.Abs(a.Z) ? a.Y : a.Z;
        if (big < 0) a = -a;
        return (Math.Abs(a.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0)).Cross(a).Normalized();
    }

    static bool IsAxial(Surface s) => s.Kind is SurfaceKind.Cylinder or SurfaceKind.Cone or SurfaceKind.Torus;

    static double OffAxis(Point3D p, Point3D on, Vector3D axis)
    {
        var v = p - on;
        return (v - axis * v.Dot(axis)).Length;
    }

    /// <summary>The axis two surfaces are both surfaces of revolution round, if any (planes across it included).</summary>
    public static (Point3D Point, Vector3D Direction)? CommonAxis(Surface a, Surface b)
    {
        if (!IsAxial(a) && IsAxial(b)) (a, b) = (b, a);
        if (IsAxial(a))
        {
            var fits = b.Kind switch
            {
                SurfaceKind.Plane => b.Axis.Cross(a.Axis).Length <= Parallel,
                SurfaceKind.Sphere => OffAxis(b.Origin, a.Origin, a.Axis) <= OnAxis,
                _ when IsAxial(b) => b.Axis.Cross(a.Axis).Length <= Parallel && OffAxis(b.Origin, a.Origin, a.Axis) <= OnAxis,
                _ => false,
            };
            return fits ? (a.Origin, a.Axis) : null;
        }
        if (a.Kind == SurfaceKind.Sphere && b.Kind == SurfaceKind.Plane) (a, b) = (b, a);
        if (a.Kind == SurfaceKind.Plane && b.Kind == SurfaceKind.Sphere) return (b.Origin, a.Axis);
        if (a.Kind == SurfaceKind.Sphere && b.Kind == SurfaceKind.Sphere && (b.Origin - a.Origin).Length > OnAxis)
            return (a.Origin, (b.Origin - a.Origin).Normalized());
        return null;
    }

    static Vector3D? CommonDirection(Surface a, Surface b)
    {
        if (a.Kind == SurfaceKind.Cylinder && b.Kind == SurfaceKind.Plane) (a, b) = (b, a);
        return (a.Kind, b.Kind) switch
        {
            (SurfaceKind.Plane, SurfaceKind.Plane) when a.Axis.Cross(b.Axis).Length > Parallel => a.Axis.Cross(b.Axis).Normalized(),
            (SurfaceKind.Plane, SurfaceKind.Cylinder) when Math.Abs(a.Axis.Dot(b.Axis)) <= Parallel => b.Axis,
            (SurfaceKind.Cylinder, SurfaceKind.Cylinder) when a.Axis.Cross(b.Axis).Length <= Parallel => a.Axis,
            _ => null,
        };
    }

    /// <summary>A line or circle in a 2D section: a point and unit direction, or a centre and radius.</summary>
    readonly record struct Shape2(double U, double V, double Du, double Dv, double R, bool Round)
    {
        public static Shape2 Line(double u, double v, double du, double dv)
        {
            var len = Math.Sqrt(du * du + dv * dv);
            return new(u, v, du / len, dv / len, 0, false);
        }

        public static Shape2 Ring(double u, double v, double r) => new(u, v, 0, 0, r, true);
    }

    /// <summary>Every circle in which two surfaces of revolution round one axis meet; none if they have no common axis.</summary>
    public static IEnumerable<Circle> Circles(Surface a, Surface b)
    {
        if (CommonAxis(a, b) is not var (on, axis) || Meridian(a, on, axis) is not { } ma || Meridian(b, on, axis) is not { } mb) yield break;
        foreach (var (u, v) in Meet(ma, mb))
            if (u > OnAxis) yield return new Circle(on + axis * v, axis, Perpendicular(axis), u);
    }

    // Origin of the section at the seed's foot keeps coordinates small, so the answer carries full precision.
    static Circle? Revolved(Surface a, Surface b, Point3D on, Vector3D axis, Point3D seed)
    {
        var origin = on + axis * axis.Dot(seed - on);
        if (Meridian(a, origin, axis) is not { } ma || Meridian(b, origin, axis) is not { } mb) return null;
        if (Nearest(ma, mb, OffAxis(seed, on, axis), 0) is not { } m || m.U <= OnAxis) return null;
        return new Circle(origin + axis * m.V, axis, Perpendicular(axis), m.U);
    }

    /// <summary>A surface of revolution round the axis through <paramref name="origin"/> as a line or circle in (ρ, z).</summary>
    static Shape2? Meridian(Surface s, Point3D origin, Vector3D axis)
    {
        var z = axis.Dot(s.Origin - origin);
        return s.Kind switch
        {
            SurfaceKind.Plane => Shape2.Line(0, z, 1, 0),
            SurfaceKind.Cylinder => Shape2.Line(s.Radius, 0, 0, 1),
            SurfaceKind.Cone => Shape2.Line(0, z, Math.Sin(s.Angle), Math.Sign(s.Axis.Dot(axis)) * Math.Cos(s.Angle)),
            SurfaceKind.Sphere => Shape2.Ring(0, z, s.Radius),
            SurfaceKind.Torus => Shape2.Ring(s.Radius, z, s.Minor),
            _ => null,
        };
    }

    static Line? Swept(Surface a, Surface b, Vector3D d, Point3D seed)
    {
        var u = Perpendicular(d);
        var v = d.Cross(u);
        Shape2? Section(Surface s)
        {
            switch (s.Kind)
            {
                case SurfaceKind.Plane:
                    var foot = seed - s.Axis * s.Axis.Dot(seed - s.Origin);
                    var t = d.Cross(s.Axis);
                    return Shape2.Line(u.Dot(foot - seed), v.Dot(foot - seed), u.Dot(t), v.Dot(t));
                case SurfaceKind.Cylinder:
                    return Shape2.Ring(u.Dot(s.Origin - seed), v.Dot(s.Origin - seed), s.Radius);
                default:
                    return null;
            }
        }
        if (Section(a) is not { } sa || Section(b) is not { } sb || Nearest(sa, sb, 0, 0) is not { } m) return null;
        return new Line(seed + u * m.U + v * m.V, d);
    }

    static Ellipse Oblique(Surface plane, Surface cylinder)
    {
        var (n, axis) = (plane.Axis, cylinder.Axis);
        var cos = n.Dot(axis);
        var centre = cylinder.Origin + axis * (n.Dot(plane.Origin - cylinder.Origin) / cos);
        var minor = axis.Cross(n).Normalized();
        return new Ellipse(centre, n, n.Cross(minor), cylinder.Radius / Math.Abs(cos), cylinder.Radius);
    }

    static (double U, double V)? Nearest(Shape2 a, Shape2 b, double u, double v) =>
        Meet(a, b).Select(p => ((double U, double V)?)p).MinBy(p => Sq(p!.Value.U - u) + Sq(p.Value.V - v));

    static IEnumerable<(double U, double V)> Meet(Shape2 a, Shape2 b)
    {
        if (a.Round && !b.Round) (a, b) = (b, a);
        if (!a.Round && !b.Round)
        {
            var cross = a.Du * b.Dv - a.Dv * b.Du;
            if (Math.Abs(cross) <= Parallel) yield break;
            var t = ((b.U - a.U) * b.Dv - (b.V - a.V) * b.Du) / cross;
            yield return (a.U + a.Du * t, a.V + a.Dv * t);
        }
        else if (!a.Round)
        {
            var along = (b.U - a.U) * a.Du + (b.V - a.V) * a.Dv;
            var (fu, fv) = (a.U + a.Du * along, a.V + a.Dv * along);
            var disc = b.R * b.R - Sq(b.U - fu) - Sq(b.V - fv);
            foreach (var s in Roots(disc, b.R)) yield return (fu + a.Du * s, fv + a.Dv * s);
        }
        else
        {
            var (du, dv) = (b.U - a.U, b.V - a.V);
            var dist = Math.Sqrt(du * du + dv * dv);
            if (dist <= OnAxis) yield break;
            var along = (a.R * a.R - b.R * b.R + dist * dist) / (2 * dist);
            var (fu, fv) = (a.U + du * along / dist, a.V + dv * along / dist);
            foreach (var s in Roots(a.R * a.R - along * along, a.R)) yield return (fu - dv / dist * s, fv + du / dist * s);
        }
    }

    /// <summary>±√disc, a single 0 for a tangent contact, nothing if apart.</summary>
    static double[] Roots(double disc, double r) =>
        Math.Abs(disc) <= Tangent * r * r ? [0] : disc < 0 ? [] : [Math.Sqrt(disc), -Math.Sqrt(disc)];

    static double Sq(double x) => x * x;
}
