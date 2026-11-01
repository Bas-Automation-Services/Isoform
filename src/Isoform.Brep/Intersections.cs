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
        (Plane p, Torus t) => PlaneTorus(p, t),
        (Torus t, Plane p) => PlaneTorus(p, t),
        (Cylinder c, Torus t) => CylinderTorus(c, t),
        (Torus t, Cylinder c) => CylinderTorus(c, t),
        (Torus t, Torus u) => SameTorus(t, u) ? SurfaceMeeting.Same : SurfaceMeeting.Refused("two tori that are not the same"),
        (Plane p, Sphere o) => PlaneSphere(p, o),
        (Sphere o, Plane p) => PlaneSphere(p, o),
        (Cylinder c, Sphere o) => CylinderSphere(c, o),
        (Sphere o, Cylinder c) => CylinderSphere(c, o),
        (Sphere o, Sphere q) => SameSphere(o, q) ? SurfaceMeeting.Same : SurfaceMeeting.Refused("two spheres that are not the same"),
        (Plane p, Cone k) => PlaneCone(p, k),
        (Cone k, Plane p) => PlaneCone(p, k),
        (Cone k, _) => Coaxial(k, b),
        (_, Cone k) => Coaxial(k, a),
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
        if (a.Cross(d.Frame.ZAxis).Length >= Parallel) return Crossed(c, d);
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

    // A plane square to the torus's axis cuts the tube in two circles round the axis (one where it touches); other
    // planes cut it in quartics, refused.
    static SurfaceMeeting PlaneTorus(Plane p, Torus t)
    {
        var (n, a) = (p.Frame.ZAxis, t.Frame.ZAxis);
        if (Math.Abs(n.Dot(a)) < 1 - Parallel) return SurfaceMeeting.Refused("a plane that is not square to a torus's axis");
        var h = (p.Frame.Origin - t.Frame.Origin).Dot(a);
        return Rings(t, h, Math.Abs(h) > t.Minor + Snap ? null : Math.Sqrt(Math.Max(0, t.Minor * t.Minor - h * h)), radial: true);
    }

    // A sphere is a torus without a hole (its section a circle on the axis): a plane square to its axis meets it in a
    // circle round the axis, a cylinder round its axis in two. Any other plane meets it in a circle round the foot of
    // the centre (one it only touches, in nothing); other cylinders are refused.
    static Torus AsTorus(Sphere o) => new(o.Frame, 0, o.Radius);

    static SurfaceMeeting PlaneSphere(Plane p, Sphere o)
    {
        var n = p.Frame.ZAxis;
        if (Math.Abs(n.Dot(o.Frame.ZAxis)) >= 1 - Parallel) return PlaneTorus(p, AsTorus(o));
        var s = n.Dot(o.Frame.Origin - p.Frame.Origin);
        if (Math.Abs(s) >= o.Radius - Snap) return SurfaceMeeting.Nothing;
        // Starting square to the sphere's axis: a meridian then starts on the equator, not at a pole.
        var x = n.Cross(o.Frame.ZAxis).Normalized();
        return SurfaceMeeting.Along(new Circle3(RigidTransform3D.FromFrame(o.Frame.Origin - n * s, n, x), Math.Sqrt(o.Radius * o.Radius - s * s)));
    }

    static SurfaceMeeting CylinderSphere(Cylinder c, Sphere o)
    {
        var w = c.Frame.Origin - o.Frame.Origin;
        return c.Frame.ZAxis.Cross(o.Frame.ZAxis).Length >= Parallel || (w - o.Frame.ZAxis * w.Dot(o.Frame.ZAxis)).Length > Snap
            ? SurfaceMeeting.Refused("a cylinder that is not round a sphere's axis")
            : CylinderTorus(c, AsTorus(o));
    }

    static bool SameSphere(Sphere o, Sphere q) => (o.Frame.Origin - q.Frame.Origin).Length <= Snap && Math.Abs(o.Radius - q.Radius) <= Snap &&
                                                 o.Frame.ZAxis.Cross(q.Frame.ZAxis).Length < Parallel;

    // A cylinder round the torus's axis cuts the tube in two circles at heights either side of its middle.
    static SurfaceMeeting CylinderTorus(Cylinder c, Torus t)
    {
        var a = t.Frame.ZAxis;
        var w = c.Frame.Origin - t.Frame.Origin;
        if (c.Frame.ZAxis.Cross(a).Length >= Parallel || (w - a * w.Dot(a)).Length > Snap)
            return SurfaceMeeting.Refused("a cylinder that is not round a torus's axis");
        var d = c.Radius - t.Major;
        return Rings(t, d, Math.Abs(d) > t.Minor + Snap ? null : Math.Sqrt(Math.Max(0, t.Minor * t.Minor - d * d)), radial: false);
    }

    // The circles round a torus's axis where the tube's section meets a line: at height h, radii Major ± half (radial),
    // or at radius Major + h, heights ± half; one circle where half is 0 (tangent), none where it is null.
    static SurfaceMeeting Rings(Torus t, double h, double? half, bool radial)
    {
        if (half is not { } w) return SurfaceMeeting.Nothing;
        var offsets = w <= Snap ? new[] { 0.0 } : [-w, w];
        var circles = new List<Curve3>();
        foreach (var o in offsets)
        {
            var (radius, height) = radial ? (t.Major + o, h) : (t.Major + h, o);
            if (radius <= Snap) continue;
            circles.Add(new Circle3(RigidTransform3D.FromFrame(t.Frame.Origin + t.Frame.ZAxis * height, t.Frame.ZAxis, t.Frame.XAxis), radius));
        }
        return SurfaceMeeting.Along([.. circles]);
    }

    // A plane square to the cone's axis meets it in a circle round the axis. A plane through the apex holds the
    // generators along which s·(n·e_r(u)) + n·Z = 0: two, or one where it touches the cone (a chamfer's plane running
    // on into a chamfer round an arc), or none. Any other plane meets it in an ellipse, parabola or hyperbola, refused.
    static SurfaceMeeting PlaneCone(Plane p, Cone k)
    {
        var (n, a) = (p.Frame.ZAxis, k.Frame.ZAxis);
        if (Math.Abs(n.Dot(a)) >= 1 - Parallel) return Ring(k, (p.Frame.Origin - k.Frame.Origin).Dot(a));
        if (Math.Abs((k.Apex - p.Frame.Origin).Dot(n)) > Snap)
            return SurfaceMeeting.Refused("a plane at an angle to a cone's axis and not through its apex (an ellipse, parabola or hyperbola)");
        var (nx, ny) = (n.Dot(k.Frame.XAxis), n.Dot(k.Frame.YAxis));
        var m = Math.Sqrt(nx * nx + ny * ny);
        var phi = Math.Atan2(ny, nx);
        // cos(u − φ) = c; at the frame's height the two generators lie R·√(1 − c²) either side of the plane's foot.
        var c = -n.Dot(a) / (k.Slope * m);
        var half = k.Radius * Math.Sqrt(Math.Abs(1 - c * c));
        if (half <= Snap) return SurfaceMeeting.Along(Generator(k, c > 0 ? phi : phi + Math.PI));
        if (Math.Abs(c) > 1) return SurfaceMeeting.Nothing;
        var delta = Math.Acos(c);
        return SurfaceMeeting.Along(Generator(k, phi - delta), Generator(k, phi + delta));
    }

    // The straight line of the cone at angle u, from its point at the frame's height, running up the axis.
    static Line3 Generator(Cone k, double u)
    {
        var (sin, cos) = Math.SinCos(u);
        var radial = k.Frame.XAxis * cos + k.Frame.YAxis * sin;
        return new Line3(k.Frame.Origin + radial * k.Radius, (radial * k.Slope + k.Frame.ZAxis).Normalized());
    }

    // The cone's circle at height h above its frame's origin; none at or past the apex.
    static SurfaceMeeting Ring(Cone k, double h)
    {
        var radius = k.RadiusAt(h);
        return radius <= Snap ? SurfaceMeeting.Nothing
            : SurfaceMeeting.Along(new Circle3(RigidTransform3D.FromFrame(k.Frame.Origin + k.Frame.ZAxis * h, k.Frame.ZAxis, k.Frame.XAxis), radius));
    }

    // A cone and a cylinder, cone or torus round the same axis meet in circles round it, where their sections in the
    // half-plane through the axis cross: the cone's is the line ρ = R + s·h (h up the cone's axis from its origin).
    static SurfaceMeeting Coaxial(Cone k, Surface other)
    {
        var a = k.Frame.ZAxis;
        var frame = other switch
        {
            Cylinder c => c.Frame,
            Cone m => m.Frame,
            Torus t => t.Frame,
            Sphere o => o.Frame,
            _ => throw new NotSupportedException(other.GetType().Name),
        };
        var w = frame.Origin - k.Frame.Origin;
        if (frame.ZAxis.Cross(a).Length >= Parallel || (w - a * w.Dot(a)).Length > Snap)
            return SurfaceMeeting.Refused($"a {other.GetType().Name.ToLowerInvariant()} that is not round a cone's axis");
        var h0 = w.Dot(a);
        switch (other)
        {
            case Cylinder c:
                return Ring(k, (c.Radius - k.Radius) / k.Slope);
            case Cone m:
            {
                // The other's section as ρ = r0 + b·h on this cone's h.
                var b = m.Frame.ZAxis.Dot(a) > 0 ? m.Slope : -m.Slope;
                var r0 = m.Radius - b * h0;
                if (Math.Abs(b - k.Slope) <= 1e-12 * (1 + Math.Abs(b)))
                    return Math.Abs(r0 - k.Radius) <= Snap ? SurfaceMeeting.Same : SurfaceMeeting.Nothing;
                return Ring(k, (r0 - k.Radius) / (k.Slope - b));
            }
            case Torus or Sphere:
            {
                var t = other as Torus ?? AsTorus((Sphere)other);
                // The line against the tube's section (a sphere's on the axis), the circle of radius r round (M, h0): the foot of the
                // perpendicular from its centre, and half a chord either side of it along the line.
                var norm = Math.Sqrt(1 + k.Slope * k.Slope);
                var gap = (k.RadiusAt(h0) - t.Major) / norm;
                if (Math.Abs(gap) > t.Minor + Snap) return SurfaceMeeting.Nothing;
                var foot = h0 - k.Slope * gap / norm;
                var chord = Math.Sqrt(Math.Max(0, t.Minor * t.Minor - gap * gap));
                if (chord <= Snap) return Ring(k, foot);
                var step = chord / norm;
                return SurfaceMeeting.Along([.. Ring(k, foot - step).Curves, .. Ring(k, foot + step).Curves]);
            }
            default:
                throw new NotSupportedException(other.GetType().Name);
        }
    }

    static bool SameTorus(Torus t, Torus u) => (t.Frame.Origin - u.Frame.Origin).Length <= Snap && t.Frame.ZAxis.Cross(u.Frame.ZAxis).Length < Parallel &&
                                               Math.Abs(t.Major - u.Major) <= Snap && Math.Abs(t.Minor - u.Minor) <= Snap;

    // Cylinders at an angle. Of equal radius with axes that cross (two fillets meeting in a mitre) they meet in two
    // ellipses, in the planes through the crossing that bisect the axes: |p|² − (p·a)² = |p|² − (p·b)² there, so
    // p·(a − b) = 0 or p·(a + b) = 0. Any other pair meets in a quartic, refused.
    static SurfaceMeeting Crossed(Cylinder c, Cylinder d)
    {
        var (a, b) = (c.Frame.ZAxis, d.Frame.ZAxis);
        var (p, q) = (new Line3(c.Frame.Origin, a), new Line3(d.Frame.Origin, b));
        var (crossing, _) = LineLine(p, q);
        if (Math.Abs(c.Radius - d.Radius) > Snap || crossing is not [var o])
            return SurfaceMeeting.Refused("two cylinders whose axes are not parallel (they meet in a quartic)");
        var curves = new List<Curve3>();
        foreach (var n in new[] { (a - b).Normalized(), (a + b).Normalized() })
        {
            var x = Math.Abs(n.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
            var plane = new Plane(RigidTransform3D.FromFrame(o, n, (x - n * n.Dot(x)).Normalized()));
            curves.AddRange(PlaneCylinder(plane, c).Curves);
        }
        return SurfaceMeeting.Along([.. curves]);
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
        (Pole q, _) => (OnConic(b, q.Point) ? [q.Point] : [], false),
        (_, Pole q) => (OnConic(a, q.Point) ? [q.Point] : [], false),
        _ => throw new NotSupportedException($"{a.GetType().Name} against {b.GetType().Name}"),
    };

    // A circle or an ellipse: its frame and semi-axes (along its X and Y).
    static (RigidTransform3D Frame, double A, double B) Conic(Curve3 c) => c switch
    {
        Circle3 k => (k.Frame, k.Radius, k.Radius),
        Ellipse3 e => (e.Frame, e.Major, e.Minor),
        _ => throw new NotSupportedException(c.GetType().Name),
    };

    // Whether a point lies on a curve (a whole circle or ellipse, a whole line, a pole's point).
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
        Pole => 0,
        _ => throw new NotSupportedException(c.GetType().Name),
    };

    /// <summary>
    /// The parameter of <paramref name="p"/> on the piece of <paramref name="c"/> over [t0, t1] (t0 &lt; t1, a circle's
    /// taken round from t0), or null when the point is not within <see cref="Snap"/> of the piece.
    /// </summary>
    public static double? On(Curve3 c, double t0, double t1, Point3D p)
    {
        if (c is Pole pole) return (pole.Point - p).Length <= Snap ? t0 : null;
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
            case Torus t:
                foreach (var hit in TorusHits(p, d, t)) yield return hit;
                break;
            case Cone k:
                foreach (var hit in ConeHits(p, d, k)) yield return hit;
                break;
            case Sphere o:
            {
                // |p + t d - O|^2 = R^2, d a unit vector.
                var from = p - o.Frame.Origin;
                var (half, rest) = (from.Dot(d), from.Dot(from) - o.Radius * o.Radius);
                var under = half * half - rest;
                if (under < 0) yield break;
                yield return -half - Math.Sqrt(under);
                yield return -half + Math.Sqrt(under);
                break;
            }
            default:
                throw new NotSupportedException(s.GetType().Name);
        }
    }

    // The ray against x² + y² = (R + s·z)² in the cone's frame, kept where R + s·z > 0 (this nappe, not the one past
    // the apex).
    static IEnumerable<double> ConeHits(Point3D p, Vector3D d, Cone k)
    {
        var (q, e) = (k.Frame.ApplyInverse(p), k.Frame.ApplyInverse(d));
        var (r0, dr) = (k.RadiusAt(q.Z), k.Slope * e.Z);
        var (qa, qb, qc) = (e.X * e.X + e.Y * e.Y - dr * dr, 2 * (q.X * e.X + q.Y * e.Y - r0 * dr), q.X * q.X + q.Y * q.Y - r0 * r0);
        var roots = new List<double>();
        if (Math.Abs(qa) < Parallel)
        {
            if (Math.Abs(qb) > Parallel) roots.Add(-qc / qb);
        }
        else
        {
            var disc = qb * qb - 4 * qa * qc;
            if (disc < 0) yield break;
            var root = Math.Sqrt(disc);
            roots.Add((-qb - root) / (2 * qa));
            roots.Add((-qb + root) / (2 * qa));
        }
        foreach (var t in roots)
            if (r0 + dr * t > 0) yield return t;
    }

    // The ray against the torus's quartic (|q|² + R² − r²)² = 4R²(qx² + qy²), within the sphere round it: sign changes
    // on a fine grid, refined by bisection. A ray grazing the tube finds no root there, as for any tangent ray.
    static IEnumerable<double> TorusHits(Point3D p, Vector3D d, Torus t)
    {
        var (q0, dq) = (t.Frame.ApplyInverse(p), t.Frame.ApplyInverse(d));
        var (big, small) = (t.Major, t.Minor);
        double F(double s)
        {
            var (x, y, z) = (q0.X + dq.X * s, q0.Y + dq.Y * s, q0.Z + dq.Z * s);
            var k = x * x + y * y + z * z + big * big - small * small;
            return k * k - 4 * big * big * (x * x + y * y);
        }
        var reach = big + small;
        var (b, c) = (q0.X * dq.X + q0.Y * dq.Y + q0.Z * dq.Z, q0.X * q0.X + q0.Y * q0.Y + q0.Z * q0.Z - reach * reach);
        var disc = b * b - c;
        if (disc < 0) yield break;
        var (from, to) = (-b - Math.Sqrt(disc), -b + Math.Sqrt(disc));
        const int n = 400;
        var previous = F(from);
        for (var i = 1; i <= n; i++)
        {
            var s = from + (to - from) * i / n;
            var value = F(s);
            if (previous * value < 0)
            {
                var (lo, hi) = (from + (to - from) * (i - 1) / n, s);
                for (var k = 0; k < 80; k++)
                {
                    var mid = (lo + hi) / 2;
                    if (F(mid) * previous > 0) lo = mid; else hi = mid;
                }
                yield return (lo + hi) / 2;
            }
            previous = value;
        }
    }

    /// <summary>Whether two surfaces are the same surface (an equal plane or cylinder), whichever way their normals point.</summary>
    public static bool SameSurface(Surface a, Surface b) => Of(a, b).Kind == Meeting.Coincident;

    static Point3D Mid(Point3D a, Point3D b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
}

/// <summary>
/// Surface parameters and the flat (u, v) the boolean works in: (u, v) itself on a plane, (R·u, v) on a cylinder (and
/// on a cone, R its radius at the frame's origin),
/// so that lengths and angles in it are the surface's own, (R·u, r·v) on a torus, where they are not, but where every
/// curve the kernel puts on one runs along u or v.
/// </summary>
internal static class Uv
{
    /// <summary>A point's (u, v); a cylinder's u in (−π, π].</summary>
    public static Point2D Of(Surface s, Point3D p) => s switch
    {
        Plane plane => new((p - plane.Frame.Origin).Dot(plane.Frame.XAxis), (p - plane.Frame.Origin).Dot(plane.Frame.YAxis)),
        Cylinder c => new(Math.Atan2((p - c.Frame.Origin).Dot(c.Frame.YAxis), (p - c.Frame.Origin).Dot(c.Frame.XAxis)),
            (p - c.Frame.Origin).Dot(c.Frame.ZAxis)),
        Torus t => TorusUv(t, p),
        Cone k => new(Math.Atan2((p - k.Frame.Origin).Dot(k.Frame.YAxis), (p - k.Frame.Origin).Dot(k.Frame.XAxis)),
            (p - k.Frame.Origin).Dot(k.Frame.ZAxis)),
        Sphere o => TorusUv(new Torus(o.Frame, 0, o.Radius), p),
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    static Point2D TorusUv(Torus t, Point3D p)
    {
        var q = t.Frame.ApplyInverse(p);
        return new Point2D(Math.Atan2(q.Y, q.X), Math.Atan2(q.Z, Math.Sqrt(q.X * q.X + q.Y * q.Y) - t.Major));
    }

    /// <summary>How flat (u, v) scales u: the radius on a cylinder, the major radius on a torus, the radius at the
    /// frame's origin on a cone.</summary>
    public static double Scale(Surface s) => s switch { Cylinder c => c.Radius, Torus t => t.Major, Cone k => k.Radius, Sphere o => o.Radius, _ => 1 };

    /// <summary>How flat (u, v) scales v: the minor radius on a torus, the radius on a sphere.</summary>
    public static double ScaleV(Surface s) => s switch { Torus t => t.Minor, Sphere o => o.Radius, _ => 1 };

    /// <summary>Which side of the surface a point is on: signed distance from a plane along its normal, from a cylinder
    /// or a torus's tube outwards.</summary>
    public static double Side(Surface s, Point3D p) => s switch
    {
        Plane plane => (p - plane.Frame.Origin).Dot(plane.Frame.ZAxis),
        Cylinder c => Radial(c, p) - c.Radius,
        Torus t => TorusSide(t, p),
        Cone k => ConeSide(k, p),
        Sphere o => (p - o.Frame.Origin).Length - o.Radius,
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    // The signed distance from the cone's section, the line ρ = R + s·h in the half-plane through the axis.
    static double ConeSide(Cone k, Point3D p)
    {
        var q = k.Frame.ApplyInverse(p);
        return (Math.Sqrt(q.X * q.X + q.Y * q.Y) - k.RadiusAt(q.Z)) / Math.Sqrt(1 + k.Slope * k.Slope);
    }

    static double TorusSide(Torus t, Point3D p)
    {
        var q = t.Frame.ApplyInverse(p);
        var rho = Math.Sqrt(q.X * q.X + q.Y * q.Y) - t.Major;
        return Math.Sqrt(rho * rho + q.Z * q.Z) - t.Minor;
    }

    /// <summary>The distance from a point to the surface.</summary>
    public static double Distance(Surface s, Point3D p) => s switch
    {
        Plane plane => Math.Abs((p - plane.Frame.Origin).Dot(plane.Frame.ZAxis)),
        Cylinder c => Math.Abs(Radial(c, p) - c.Radius),
        Torus t => TorusDistance(t, p),
        Cone k => Math.Abs(ConeSide(k, p)),
        Sphere o => Math.Abs((p - o.Frame.Origin).Length - o.Radius),
        _ => throw new NotSupportedException(s.GetType().Name),
    };

    static double TorusDistance(Torus t, Point3D p)
    {
        var q = t.Frame.ApplyInverse(p);
        var rho = Math.Sqrt(q.X * q.X + q.Y * q.Y) - t.Major;
        return Math.Abs(Math.Sqrt(rho * rho + q.Z * q.Z) - t.Minor);
    }

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
            case (Cone k, Line3 l):
            {
                // A generator: u fixed, v runs with t. Its angle comes from the direction, whose part away from the
                // axis points along e_r(u) as the line climbs the slope, so the origin may lie anywhere on it.
                var z = k.Frame.ZAxis;
                var away = (l.Direction - z * l.Direction.Dot(z)) * Math.Sign(k.Slope * l.Direction.Dot(z));
                var u = Math.Atan2(away.Dot(k.Frame.YAxis), away.Dot(k.Frame.XAxis));
                return new Line2(new Point2D(u, (l.Origin - k.Frame.Origin).Dot(z)), new Vector2D(0, l.Direction.Dot(z)));
            }
            case (Cone cone, Circle3 k) when k.Frame.ZAxis.Cross(cone.Frame.ZAxis).Length < 1e-9:
            {
                // Round the axis: v fixed, u runs with t.
                var x = k.Frame.XAxis;
                var phase = Math.Atan2(x.Dot(cone.Frame.YAxis), x.Dot(cone.Frame.XAxis));
                var sense = k.Frame.ZAxis.Dot(cone.Frame.ZAxis) > 0 ? 1 : -1;
                return new Line2(new Point2D(phase, (k.Frame.Origin - cone.Frame.Origin).Dot(cone.Frame.ZAxis)), new Vector2D(sense, 0));
            }
            case (Torus t, Circle3 k) when k.Frame.ZAxis.Cross(t.Frame.ZAxis).Length < 1e-9:
            {
                // Round the axis: v fixed, u runs with t.
                var x = k.Frame.XAxis;
                var phase = Math.Atan2(x.Dot(t.Frame.YAxis), x.Dot(t.Frame.XAxis));
                var v = TorusUv(t, k.At(0)).Y;
                return new Line2(new Point2D(phase, v), new Vector2D(k.Frame.ZAxis.Dot(t.Frame.ZAxis) > 0 ? 1 : -1, 0));
            }
            case (Torus t, Circle3 k):
            {
                // Round the tube (a meridian): u fixed, v runs with t.
                var u = TorusUv(t, k.Frame.Origin).X;
                var (su, cu) = Math.SinCos(u);
                var radial = t.Frame.XAxis * cu + t.Frame.YAxis * su;
                var (x, y) = (k.Frame.XAxis, k.Frame.YAxis);
                var psi = Math.Atan2(x.Dot(t.Frame.ZAxis), x.Dot(radial));
                var turning = radial * -Math.Sin(psi) + t.Frame.ZAxis * Math.Cos(psi);
                return new Line2(new Point2D(u, psi), new Vector2D(0, y.Dot(turning) > 0 ? 1 : -1));
            }
            case (Sphere o, Circle3 k) when k.Frame.ZAxis.Cross(o.Frame.ZAxis).Length < 1e-9:
            {
                // A parallel: v fixed, u runs with t.
                var x = k.Frame.XAxis;
                var phase = Math.Atan2(x.Dot(o.Frame.YAxis), x.Dot(o.Frame.XAxis));
                var v = Of(o, k.At(0)).Y;
                return new Line2(new Point2D(phase, v), new Vector2D(k.Frame.ZAxis.Dot(o.Frame.ZAxis) > 0 ? 1 : -1, 0));
            }
            case (Sphere o, Circle3 k) when Math.Abs(k.Frame.ZAxis.Dot(o.Frame.ZAxis)) > 1e-9 || (k.Frame.Origin - o.Frame.Origin).Length > Intersections.Snap:
                // Any other circle: longitude and latitude along it.
                return SphereCircle2.Of(o, k) ?? throw new NotSupportedException("a circle through a sphere's pole");
            case (Sphere o, Circle3 k):
            {
                // A meridian (a great circle through the poles, round the centre): u fixed by the half-plane its X
                // points into, v runs with t.
                var (x, y, z) = (k.Frame.XAxis, k.Frame.YAxis, o.Frame.ZAxis);
                var radial = (x - z * x.Dot(z)).Normalized();
                var u = Math.Atan2(radial.Dot(o.Frame.YAxis), radial.Dot(o.Frame.XAxis));
                var psi = Math.Atan2(x.Dot(z), x.Dot(radial));
                var turning = radial * -Math.Sin(psi) + z * Math.Cos(psi);
                return new Line2(new Point2D(u, psi), new Vector2D(0, y.Dot(turning) > 0 ? 1 : -1));
            }
            case (Sphere o, Pole q):
                // A pole: v at plus or minus a quarter turn, u runs with t.
                return new Line2(new Point2D(0, Math.Sign((q.Point - o.Frame.Origin).Dot(o.Frame.ZAxis)) * Math.PI / 2), new Vector2D(1, 0));
            case (Cone k, Pole):
                // The apex: v where the radius is zero, u runs with t.
                return new Line2(new Point2D(0, -k.Radius / k.Slope), new Vector2D(1, 0));
            default:
                throw new NotSupportedException($"a {c.GetType().Name} on a {s.GetType().Name}");
        }
    }

    /// <summary>
    /// A meridian's p-curve for the piece round <paramref name="t"/>: a meridian's line runs on in v past the poles,
    /// where the sphere's point at (u, v) is the one at (u + π, ±π − v), so a piece beyond a pole takes that line.
    /// </summary>
    public static Curve2 Within(Surface s, Curve2 pc, double t)
    {
        if (s is not Sphere || pc is not Line2 { Direction.X: 0 } l) return pc;
        var v = l.At(t).Y;
        var turns = 2 * Math.PI * Math.Round(v / (2 * Math.PI));
        (l, v) = (l with { Origin = new Point2D(l.Origin.X, l.Origin.Y - turns) }, v - turns);
        if (Math.Abs(v) <= Math.PI / 2) return l;
        var side = Math.Sign(v) * Math.PI;
        return new Line2(new Point2D(l.Origin.X + Math.PI, side - l.Origin.Y), new Vector2D(0, -l.Direction.Y));
    }

    /// <summary>The p-curve moved by <paramref name="du"/> in u (and <paramref name="dv"/> in v).</summary>
    public static Curve2 Shift(Curve2 pc, double du, double dv = 0) => du == 0 && dv == 0 ? pc : dv != 0 ? pc switch
    {
        Line2 l => l with { Origin = new Point2D(l.Origin.X + du, l.Origin.Y + dv) },
        _ => throw new NotSupportedException($"a {pc.GetType().Name} moved in v"),
    } : pc switch
    {
        Line2 l => l with { Origin = new Point2D(l.Origin.X + du, l.Origin.Y) },
        Circle2 c => c with { Center = new Point2D(c.Center.X + du, c.Center.Y) },
        Ellipse2 e => e with { Center = new Point2D(e.Center.X + du, e.Center.Y) },
        Wave2 w => w with { U0 = w.U0 + du },
        SphereCircle2 c => c with { U0 = c.U0 + du },
        _ => throw new NotSupportedException(pc.GetType().Name),
    };

    /// <summary>The p-curve as a function of s with t = α·s + β (α = ±1).</summary>
    public static Curve2 Reparametrised(Curve2 pc, double alpha, double beta) => pc switch
    {
        Line2 l => new Line2(l.At(beta), l.Direction * alpha),
        Circle2 c => new Circle2(c.Center, c.Radius, c.Phase + c.Sense * beta, c.Sense * (int)Math.Round(alpha)),
        Ellipse2 e => e with { Offset = alpha * (beta + e.Offset), Sense = e.Sense * (int)Math.Round(alpha) },
        Wave2 w => w with { U0 = w.U0 + w.Slope * beta, Slope = w.Slope * (int)Math.Round(alpha), Phase = alpha * (beta + w.Phase) },
        SphereCircle2 c => c with { Phase = c.Phase + c.Sense * beta, Sense = c.Sense * (int)Math.Round(alpha) },
        _ => throw new NotSupportedException(pc.GetType().Name),
    };

    /// <summary>The p-curve over [from, to] (in that order) as a segment in flat (u, v).</summary>
    public static Segment Flat(Surface s, Curve2 pc, double from, double to)
    {
        var (k, kv) = (Scale(s), ScaleV(s));
        switch (pc)
        {
            case Line2 l:
                Point2D F(Point2D p) => new(p.X * k, p.Y * kv);
                return new LineSegment(F(l.At(from)), F(l.At(to)));
            case Circle2 c when k == 1:
                return new ArcSegment(c.Center, c.Radius, c.Phase + c.Sense * from, c.Sense * (to - from));
            case Ellipse2 when k == 1:
            case Wave2 when s is Cylinder:
                return new CurveSegment(pc, from, to, k);
            case SphereCircle2 when s is Sphere:
                return new CurveSegment(pc, from, to, k, kv);
            default:
                throw new NotSupportedException($"a {pc.GetType().Name} on a {s.GetType().Name}");
        }
    }
}
