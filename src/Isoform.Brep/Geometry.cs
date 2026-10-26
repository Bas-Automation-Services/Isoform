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
    public Vector3D Normal(double u, double v) => DerivativeU(u, v).Cross(DerivativeV(u, v)).Normalized();

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
