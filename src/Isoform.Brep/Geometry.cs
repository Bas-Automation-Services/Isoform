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
