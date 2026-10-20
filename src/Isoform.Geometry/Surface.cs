using BasAutomation.Geometry;

namespace Isoform.Geometry;

public enum SurfaceKind { Plane, Cylinder, Fillet, Sphere, Cone, Torus }

/// <summary>
/// An analytic surface with a 1-Lipschitz signed distance, exact near the surface: negative on the material side.
/// <list type="bullet">
/// <item>Plane: <see cref="Origin"/> on the plane, <see cref="Axis"/> the outward normal.</item>
/// <item>Cylinder: <see cref="Origin"/> on the axis, <see cref="Axis"/> its direction, material inside <see cref="Radius"/>.</item>
/// <item>Sphere: centre <see cref="Origin"/>, material inside <see cref="Radius"/>.</item>
/// <item>Cone: apex <see cref="Origin"/>, opening along <see cref="Axis"/> with half-angle <see cref="Angle"/>, material inside.</item>
/// <item>Torus: centre <see cref="Origin"/>, <see cref="Axis"/>, centre circle <see cref="Radius"/>, material inside tube <see cref="Minor"/>.</item>
/// <item>Fillet: produced by a rounded edge; its distance comes from <see cref="RoundedEdgeField"/>.</item>
/// </list>
/// Cone and torus are a line and a circle in the half-plane (ρ, z) of distance from the axis and height along it:
/// their distance is the 2D distance there, which is how a revolved profile edge measures it too.
/// </summary>
public sealed record Surface(int Id, string Source, SurfaceKind Kind, Point3D Origin, Vector3D Axis, double Radius = 0, double Minor = 0, double Angle = 0)
{
    public double Distance(Point3D p) => Kind switch
    {
        SurfaceKind.Plane => Axis.Dot(p - Origin),
        SurfaceKind.Cylinder => Radial(p).Length - Radius,
        SurfaceKind.Sphere => (p - Origin).Length - Radius,
        SurfaceKind.Cone => Radial(p).Length * Math.Cos(Angle) - Axis.Dot(p - Origin) * Math.Sin(Angle),
        SurfaceKind.Torus => Hypot(Radial(p).Length - Radius, Axis.Dot(p - Origin)) - Minor,
        _ => throw new InvalidOperationException("Fillet surfaces have no standalone distance."),
    };

    public Vector3D Gradient(Point3D p)
    {
        switch (Kind)
        {
            case SurfaceKind.Plane:
                return Axis;
            case SurfaceKind.Sphere:
                var d = p - Origin;
                var len = d.Length;
                return len > 0 ? d / len : Axis;
            case SurfaceKind.Cone:
                return RadialUnit(p) * Math.Cos(Angle) - Axis * Math.Sin(Angle);
            case SurfaceKind.Torus:
                var (rho, z) = (Radial(p).Length - Radius, Axis.Dot(p - Origin));
                var tube = Hypot(rho, z);
                return tube > 0 ? (RadialUnit(p) * rho + Axis * z) / tube : Axis;
            default:
                return RadialUnit(p);
        }
    }

    /// <summary>The surface after the material grows by <paramref name="d"/> (negative: shrinks).</summary>
    public Surface Offset(int id, string source, double d) => Kind switch
    {
        SurfaceKind.Plane => new(id, source, Kind, Origin + Axis * d, Axis),
        SurfaceKind.Cone => new(id, source, Kind, Origin - Axis * (d / Math.Sin(Angle)), Axis, Angle: Angle),
        SurfaceKind.Torus => new(id, source, Kind, Origin, Axis, Radius, Minor + d),
        _ => new(id, source, Kind, Origin, Axis, Radius + d),
    };

    /// <summary>True if both describe the same geometric surface with the same material side.</summary>
    public bool SameGeometry(Surface other, double tolerance)
    {
        if (Kind != other.Kind || Kind == SurfaceKind.Fillet) return false;
        bool Near(double a, double b) => Math.Abs(a - b) <= tolerance;
        bool Coaxial() => Axis.Cross(other.Axis).Length <= tolerance && Radial(other.Origin).Length <= tolerance;
        return Kind switch
        {
            SurfaceKind.Plane => (Axis - other.Axis).Length <= tolerance && Math.Abs(Distance(other.Origin)) <= tolerance,
            SurfaceKind.Cylinder => Near(Radius, other.Radius) && Coaxial(),
            SurfaceKind.Sphere => Near(Radius, other.Radius) && (Origin - other.Origin).Length <= tolerance,
            SurfaceKind.Cone => Near(Angle, other.Angle) && (Axis - other.Axis).Length <= tolerance && (Origin - other.Origin).Length <= tolerance,
            _ => Near(Radius, other.Radius) && Near(Minor, other.Minor) && Coaxial() && Math.Abs(Axis.Dot(other.Origin - Origin)) <= tolerance,
        };
    }

    Vector3D Radial(Point3D p)
    {
        var v = p - Origin;
        return v - Axis * v.Dot(Axis);
    }

    Vector3D RadialUnit(Point3D p)
    {
        var r = Radial(p);
        var len = r.Length;
        // On the axis any radial direction is valid; pick one deterministically.
        return len > 0 ? r / len : AnyPerpendicular(Axis);
    }

    static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

    static Vector3D AnyPerpendicular(Vector3D v) =>
        (Math.Abs(v.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0)).Cross(v).Normalized();
}

/// <summary>An orthonormal frame; a mirrored (left-handed) one is allowed.</summary>
public readonly record struct Frame(Point3D Origin, Vector3D X, Vector3D Y, Vector3D Z)
{
    public static Frame Identity => new(new Point3D(0, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0), new Vector3D(0, 0, 1));

    /// <summary>A right-handed frame from an origin, an in-plane x direction and a normal: y = normal × x.</summary>
    public static Frame FromXAndNormal(Point3D origin, Vector3D x, Vector3D normal)
    {
        var z = normal.Normalized();
        var xx = (x - z * x.Dot(z)).Normalized();
        return new(origin, xx, z.Cross(xx), z);
    }

    public Point3D ToWorld(Point3D p) => Origin + X * p.X + Y * p.Y + Z * p.Z;
    public Vector3D ToWorld(Vector3D v) => X * v.X + Y * v.Y + Z * v.Z;

    /// <summary>This frame placed by <paramref name="by"/>: its axes and origin expressed in <paramref name="by"/>'s world.</summary>
    public Frame Then(Frame by) => new(by.ToWorld(Origin), by.ToWorld(X), by.ToWorld(Y), by.ToWorld(Z));
}
