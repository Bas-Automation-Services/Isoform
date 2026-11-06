using BasAutomation.Geometry;

namespace Isoform.Brep;

// Carrier geometry: unbounded curves and surfaces with their natural parameters. Topology trims them (an edge is a
// curve over [T0, T1], a face a surface inside its loops). Frames are right-handed (RigidTransform3D), so a
// direction of rotation is always "counter-clockwise about the frame's Z" and a reversed sense is a flipped Z.

/// <summary>A curve in space, parametrised by <c>t</c>.</summary>
public abstract record Curve3
{
    public abstract Point3D At(double t);

    /// <summary>dC/dt.</summary>
    public abstract Vector3D Derivative(double t);

    /// <summary>The parameter's period for a closed curve (2π for a circle), else null.</summary>
    public virtual double? Period => null;
}

/// <summary><c>Origin + t · Direction</c> with a unit direction, so <c>t</c> is arc length.</summary>
public sealed record Line3(Point3D Origin, Vector3D Direction) : Curve3
{
    public override Point3D At(double t) => Origin + Direction * t;
    public override Vector3D Derivative(double t) => Direction;
}

/// <summary>
/// A degenerate curve, one point for every t: the edge where a face's (u, v) boundary runs along a pole of its surface
/// (a sphere's), used once, by that face. t is the u it runs over.
/// </summary>
public sealed record Pole(Point3D Point) : Curve3
{
    public override Point3D At(double t) => Point;
    public override Vector3D Derivative(double t) => new(0, 0, 0);
}

/// <summary>
/// A circle in its frame's XY plane round the frame's origin, counter-clockwise about the frame's Z:
/// <c>Origin + Radius · (cos t · X + sin t · Y)</c>. <c>t</c> is the angle from X.
/// </summary>
public sealed record Circle3(RigidTransform3D Frame, double Radius) : Curve3
{
    public override Point3D At(double t)
    {
        var (sin, cos) = Math.SinCos(t);
        return Frame.Origin + (Frame.XAxis * cos + Frame.YAxis * sin) * Radius;
    }

    public override Vector3D Derivative(double t)
    {
        var (sin, cos) = Math.SinCos(t);
        return (Frame.YAxis * cos - Frame.XAxis * sin) * Radius;
    }

    public override double? Period => 2 * Math.PI;
}

/// <summary>A curve in a surface's (u, v) parameters: a p-curve.</summary>
public abstract record Curve2
{
    public abstract Point2D At(double t);

    /// <summary>d(u, v)/dt.</summary>
    public abstract Vector2D Derivative(double t);
}

/// <summary><c>Origin + t · Direction</c>; the direction need not be unit, since a p-curve's speed in (u, v) is the edge's.</summary>
public sealed record Line2(Point2D Origin, Vector2D Direction) : Curve2
{
    public override Point2D At(double t) => Origin + Direction * t;
    public override Vector2D Derivative(double t) => Direction;
}

/// <summary>
/// <c>Center + Radius · (cos θ, sin θ)</c> with <c>θ = Phase + Sense · t</c>: counter-clockwise for Sense +1,
/// clockwise for −1.
/// </summary>
public sealed record Circle2(Point2D Center, double Radius, double Phase, int Sense) : Curve2
{
    public override Point2D At(double t)
    {
        var (sin, cos) = Math.SinCos(Phase + Sense * t);
        return new Point2D(Center.X + Radius * cos, Center.Y + Radius * sin);
    }

    public override Vector2D Derivative(double t)
    {
        var (sin, cos) = Math.SinCos(Phase + Sense * t);
        return new Vector2D(-Radius * Sense * sin, Radius * Sense * cos);
    }
}

/// <summary>A surface S(u, v). Its normal is along Su × Sv; a face says whether that points out of the solid.</summary>
public abstract record Surface
{
    public abstract Point3D At(double u, double v);

    /// <summary>∂S/∂u.</summary>
    public abstract Vector3D DerivativeU(double u, double v);

    /// <summary>∂S/∂v.</summary>
    public abstract Vector3D DerivativeV(double u, double v);

    /// <summary>The unit normal, along Su × Sv.</summary>
    public virtual Vector3D Normal(double u, double v) => DerivativeU(u, v).Cross(DerivativeV(u, v)).Normalized();

    /// <summary>The period of u for a surface closed round u (2π for a cylinder), else null.</summary>
    public virtual double? PeriodU => null;

    /// <summary>The period of v for a surface closed round v (2π round a torus's tube), else null.</summary>
    public virtual double? PeriodV => null;
}

/// <summary>The frame's XY plane: <c>S(u, v) = Origin + u · X + v · Y</c>, normal along Z.</summary>
public sealed record Plane(RigidTransform3D Frame) : Surface
{
    public override Point3D At(double u, double v) => Frame.Origin + Frame.XAxis * u + Frame.YAxis * v;
    public override Vector3D DerivativeU(double u, double v) => Frame.XAxis;
    public override Vector3D DerivativeV(double u, double v) => Frame.YAxis;
}

/// <summary>
/// A cylinder round the frame's Z axis: <c>S(u, v) = Origin + Radius · (cos u · X + sin u · Y) + v · Z</c>. u is the
/// angle from X (periodic, 2π), v the height; the normal points away from the axis.
/// </summary>
public sealed record Cylinder(RigidTransform3D Frame, double Radius) : Surface
{
    public override Point3D At(double u, double v)
    {
        var (sin, cos) = Math.SinCos(u);
        return Frame.Origin + (Frame.XAxis * cos + Frame.YAxis * sin) * Radius + Frame.ZAxis * v;
    }

    public override Vector3D DerivativeU(double u, double v)
    {
        var (sin, cos) = Math.SinCos(u);
        return (Frame.YAxis * cos - Frame.XAxis * sin) * Radius;
    }

    public override Vector3D DerivativeV(double u, double v) => Frame.ZAxis;

    public override double? PeriodU => 2 * Math.PI;
}

/// <summary>
/// A cone round the frame's Z axis: <c>S(u, v) = Origin + (Radius + Slope · v) · (cos u · X + sin u · Y) + v · Z</c>.
/// u is the angle from X (periodic, 2π), v the height; the radius grows by <see cref="Slope"/> per unit up Z (shrinks
/// where it is negative) and is above zero at the frame's origin, so the apex lies at v = −Radius / Slope. The normal
/// points away from the axis, tilted back along the slope. A chamfer round a circle is a piece of one.
/// </summary>
public sealed record Cone(RigidTransform3D Frame, double Radius, double Slope) : Surface
{
    /// <summary>The distance from the axis at height v.</summary>
    public double RadiusAt(double v) => Radius + Slope * v;

    /// <summary>The apex, where the radius is zero.</summary>
    public Point3D Apex => Frame.Origin - Frame.ZAxis * (Radius / Slope);

    public override Point3D At(double u, double v)
    {
        var (sin, cos) = Math.SinCos(u);
        return Frame.Origin + (Frame.XAxis * cos + Frame.YAxis * sin) * RadiusAt(v) + Frame.ZAxis * v;
    }

    public override Vector3D DerivativeU(double u, double v)
    {
        var (sin, cos) = Math.SinCos(u);
        return (Frame.YAxis * cos - Frame.XAxis * sin) * RadiusAt(v);
    }

    public override Vector3D DerivativeV(double u, double v)
    {
        var (sin, cos) = Math.SinCos(u);
        return (Frame.XAxis * cos + Frame.YAxis * sin) * Slope + Frame.ZAxis;
    }

    /// <summary>Its own, the same all along a generator: Su vanishes at the apex. Along e_u × Sv = e_r − Slope · Z.</summary>
    public override Vector3D Normal(double u, double v)
    {
        var (sin, cos) = Math.SinCos(u);
        return ((Frame.XAxis * cos + Frame.YAxis * sin) - Frame.ZAxis * Slope) / Math.Sqrt(1 + Slope * Slope);
    }

    public override double? PeriodU => 2 * Math.PI;
}

/// <summary>
/// A torus round the frame's Z axis: <c>S(u, v) = Origin + (Major + Minor · cos v) · (cos u · X + sin u · Y) + Minor ·
/// sin v · Z</c>. u is the angle round the axis, v round the tube from its outer side; the normal points out of the
/// tube. A fillet between a plane and a cylinder square to it is a piece of one.
/// </summary>
public sealed record Torus(RigidTransform3D Frame, double Major, double Minor) : Surface
{
    public override Point3D At(double u, double v)
    {
        var (su, cu) = Math.SinCos(u);
        var (sv, cv) = Math.SinCos(v);
        return Frame.Origin + (Frame.XAxis * cu + Frame.YAxis * su) * (Major + Minor * cv) + Frame.ZAxis * (Minor * sv);
    }

    public override Vector3D DerivativeU(double u, double v)
    {
        var (su, cu) = Math.SinCos(u);
        return (Frame.YAxis * cu - Frame.XAxis * su) * (Major + Minor * Math.Cos(v));
    }

    public override Vector3D DerivativeV(double u, double v)
    {
        var (su, cu) = Math.SinCos(u);
        var (sv, cv) = Math.SinCos(v);
        return (Frame.XAxis * cu + Frame.YAxis * su) * (-Minor * sv) + Frame.ZAxis * (Minor * cv);
    }

    public override double? PeriodU => 2 * Math.PI;
    public override double? PeriodV => 2 * Math.PI;
}

/// <summary>
/// An ellipse in its frame's XY plane round the frame's origin, counter-clockwise about the frame's Z:
/// <c>Origin + Major · cos t · X + Minor · sin t · Y</c>. A plane at an angle to a cylinder meets it in one.
/// </summary>
public sealed record Ellipse3(RigidTransform3D Frame, double Major, double Minor) : Curve3
{
    public override Point3D At(double t)
    {
        var (sin, cos) = Math.SinCos(t);
        return Frame.Origin + Frame.XAxis * (Major * cos) + Frame.YAxis * (Minor * sin);
    }

    public override Vector3D Derivative(double t)
    {
        var (sin, cos) = Math.SinCos(t);
        return Frame.YAxis * (Minor * cos) - Frame.XAxis * (Major * sin);
    }

    public override double? Period => 2 * Math.PI;
}

/// <summary>
/// An ellipse in (u, v): <c>Center + Major · cos(t + Offset) · a + Minor · sin(t + Offset) · Sense · b</c>, with a the
/// direction at angle <see cref="Rotation"/> and b a turned a quarter counter-clockwise.
/// </summary>
public sealed record Ellipse2(Point2D Center, double Major, double Minor, double Rotation, int Sense, double Offset) : Curve2
{
    public override Point2D At(double t)
    {
        var (s, c) = Math.SinCos(t + Offset);
        var (rs, rc) = Math.SinCos(Rotation);
        var (x, y) = (Major * c, Minor * s * Sense);
        return new Point2D(Center.X + x * rc - y * rs, Center.Y + x * rs + y * rc);
    }

    public override Vector2D Derivative(double t)
    {
        var (s, c) = Math.SinCos(t + Offset);
        var (rs, rc) = Math.SinCos(Rotation);
        var (x, y) = (-Major * s, Minor * c * Sense);
        return new Vector2D(x * rc - y * rs, x * rs + y * rc);
    }
}

/// <summary>
/// A plane's section of a cylinder in its (u, v): <c>u = U0 + Slope · t</c> (Slope ±1) and
/// <c>v = V0 + Amplitude · cos(t + Phase)</c>, for an ellipse parametrised by its angle t.
/// </summary>
public sealed record Wave2(double U0, double V0, int Slope, double Amplitude, double Phase) : Curve2
{
    public override Point2D At(double t) => new(U0 + Slope * t, V0 + Amplitude * Math.Cos(t + Phase));

    public override Vector2D Derivative(double t) => new(Slope, -Amplitude * Math.Sin(t + Phase));
}

/// <summary>
/// A circle on a sphere in its (u, v): the point at angle θ = Phase + Sense · t round the circle, as longitude u and
/// latitude v. u runs on continuously, within half a turn of U0 + Winding · θ: Winding ±1 for a circle round the
/// sphere's axis (as θ turns, u turns once), 0 for one beside it. A circle through a pole has no p-curve.
/// </summary>
public sealed record SphereCircle2(Sphere Sphere, Circle3 Circle, double Phase, int Sense, double U0, int Winding) : Curve2
{
    /// <summary>The p-curve of the circle with θ = t; null for a circle through a pole.</summary>
    public static SphereCircle2? Of(Sphere sphere, Circle3 circle)
    {
        var (z, n) = (sphere.Frame.ZAxis, circle.Frame.ZAxis);
        // Where the axis crosses the circle's plane: inside the circle, it winds round the axis.
        var winding = 0;
        if (Math.Abs(n.Dot(z)) > 1e-12)
        {
            var hit = sphere.Frame.Origin + z * (n.Dot(circle.Frame.Origin - sphere.Frame.Origin) / n.Dot(z));
            var off = (hit - circle.Frame.Origin).Length;
            if (Math.Abs(off - circle.Radius) <= 1e-9 * (1 + circle.Radius)) return null;
            if (off < circle.Radius) winding = Math.Sign(n.Dot(z));
        }
        var p = new SphereCircle2(sphere, circle, 0, 1, 0, winding).Local(0).P;
        return new SphereCircle2(sphere, circle, 0, 1, Math.Atan2(p.Y, p.X), winding);
    }

    // The point at t less the sphere's centre, and its derivative, in the sphere's frame.
    (Vector3D P, Vector3D D) Local(double t)
    {
        var (sin, cos) = Math.SinCos(Phase + Sense * t);
        var f = Circle.Frame;
        var p = f.Origin - Sphere.Frame.Origin + (f.XAxis * cos + f.YAxis * sin) * Circle.Radius;
        var d = (f.YAxis * cos - f.XAxis * sin) * (Circle.Radius * Sense);
        var (x, y, z) = (Sphere.Frame.XAxis, Sphere.Frame.YAxis, Sphere.Frame.ZAxis);
        return (new Vector3D(p.Dot(x), p.Dot(y), p.Dot(z)), new Vector3D(d.Dot(x), d.Dot(y), d.Dot(z)));
    }

    public override Point2D At(double t)
    {
        var (p, _) = Local(t);
        var near = U0 + Winding * (Phase + Sense * t);
        var u = Math.Atan2(p.Y, p.X);
        return new Point2D(u + 2 * Math.PI * Math.Round((near - u) / (2 * Math.PI)), Math.Asin(Math.Clamp(p.Z / Sphere.Radius, -1, 1)));
    }

    // u = atan2(y, x), v = asin(z / R): u' = (x y' − y x') / ρ², v' = z' / ρ with ρ² = x² + y² = R² − z².
    public override Vector2D Derivative(double t)
    {
        var (p, d) = Local(t);
        var rho2 = p.X * p.X + p.Y * p.Y;
        return new Vector2D((p.X * d.Y - p.Y * d.X) / rho2, d.Z / Math.Sqrt(rho2));
    }
}

/// <summary>
/// A plane's ellipse round a cone's axis in the cone's (u, v): the point at angle θ = Phase + Sense · t round the ellipse,
/// as the angle u round the axis and the height v along it. u runs on continuously, within half a turn of
/// U0 + Winding · θ: the ellipse goes once round the axis, Winding ±1 by which way.
/// </summary>
public sealed record ConeSection2(Cone Cone, Ellipse3 Ellipse, double Phase, int Sense, double U0, int Winding) : Curve2
{
    /// <summary>The p-curve of the ellipse with θ = t.</summary>
    public static ConeSection2 Of(Cone cone, Ellipse3 ellipse)
    {
        var winding = ellipse.Frame.ZAxis.Dot(cone.Frame.ZAxis) > 0 ? 1 : -1;
        var p = new ConeSection2(cone, ellipse, 0, 1, 0, winding).Local(0).P;
        return new ConeSection2(cone, ellipse, 0, 1, Math.Atan2(p.Y, p.X), winding);
    }

    // The point at t less the cone's origin, and its derivative, in the cone's frame.
    (Vector3D P, Vector3D D) Local(double t)
    {
        var (sin, cos) = Math.SinCos(Phase + Sense * t);
        var f = Ellipse.Frame;
        var p = f.Origin - Cone.Frame.Origin + f.XAxis * (Ellipse.Major * cos) + f.YAxis * (Ellipse.Minor * sin);
        var d = (f.YAxis * (Ellipse.Minor * cos) - f.XAxis * (Ellipse.Major * sin)) * Sense;
        var (x, y, z) = (Cone.Frame.XAxis, Cone.Frame.YAxis, Cone.Frame.ZAxis);
        return (new Vector3D(p.Dot(x), p.Dot(y), p.Dot(z)), new Vector3D(d.Dot(x), d.Dot(y), d.Dot(z)));
    }

    public override Point2D At(double t)
    {
        var (p, _) = Local(t);
        var near = U0 + Winding * (Phase + Sense * t);
        var u = Math.Atan2(p.Y, p.X);
        return new Point2D(u + 2 * Math.PI * Math.Round((near - u) / (2 * Math.PI)), p.Z);
    }

    // u = atan2(y, x), v = z: u' = (x y' − y x') / (x² + y²), v' = z'.
    public override Vector2D Derivative(double t)
    {
        var (p, d) = Local(t);
        return new Vector2D((p.X * d.Y - p.Y * d.X) / (p.X * p.X + p.Y * p.Y), d.Z);
    }
}

/// <summary>
/// A sphere round the frame's origin: <c>S(u, v) = Origin + Radius · (cos v · (cos u · X + sin u · Y) + sin v · Z)</c>.
/// u is the angle round Z (periodic), v the latitude in [−π/2, π/2]; at the poles, ±Z, u runs over a single point. The
/// normal points out. A dome where a revolve's arc ends on its axis is a piece of one.
/// </summary>
public sealed record Sphere(RigidTransform3D Frame, double Radius) : Surface
{
    public override Point3D At(double u, double v)
    {
        var (su, cu) = Math.SinCos(u);
        var (sv, cv) = Math.SinCos(v);
        return Frame.Origin + (Frame.XAxis * cu + Frame.YAxis * su) * (Radius * cv) + Frame.ZAxis * (Radius * sv);
    }

    public override Vector3D DerivativeU(double u, double v)
    {
        var (su, cu) = Math.SinCos(u);
        return (Frame.YAxis * cu - Frame.XAxis * su) * (Radius * Math.Cos(v));
    }

    public override Vector3D DerivativeV(double u, double v)
    {
        var (su, cu) = Math.SinCos(u);
        var (sv, cv) = Math.SinCos(v);
        return (Frame.XAxis * cu + Frame.YAxis * su) * (-Radius * sv) + Frame.ZAxis * (Radius * cv);
    }

    /// <summary>Outwards, the poles included (where Su vanishes).</summary>
    public override Vector3D Normal(double u, double v)
    {
        var (su, cu) = Math.SinCos(u);
        var (sv, cv) = Math.SinCos(v);
        return (Frame.XAxis * cu + Frame.YAxis * su) * cv + Frame.ZAxis * sv;
    }

    public override double? PeriodU => 2 * Math.PI;
}
