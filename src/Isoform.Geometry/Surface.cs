using BasAutomation.Geometry;

namespace Isoform.Geometry;

public enum SurfaceKind { Plane, Cylinder, Fillet }

/// <summary>
/// An analytic surface with an exact signed distance: negative on the material side.
/// Plane: <see cref="Origin"/> on the plane, <see cref="Axis"/> the outward normal.
/// Cylinder: <see cref="Origin"/> on the axis, <see cref="Axis"/> its direction, material inside <see cref="Radius"/>.
/// Fillet: produced by a rounded edge; its distance comes from <see cref="RoundedEdgeField"/>.
/// </summary>
public sealed record Surface(int Id, string Source, SurfaceKind Kind, Point3D Origin, Vector3D Axis, double Radius = 0)
{
    public double Distance(Point3D p) => Kind switch
    {
        SurfaceKind.Plane => Axis.Dot(p - Origin),
        SurfaceKind.Cylinder => Radial(p).Length - Radius,
        _ => throw new InvalidOperationException("Fillet surfaces have no standalone distance."),
    };

    public Vector3D Gradient(Point3D p)
    {
        if (Kind == SurfaceKind.Plane) return Axis;
        var r = Radial(p);
        var len = r.Length;
        // On the axis any radial direction is valid; pick one deterministically.
        return len > 0 ? r / len : AnyPerpendicular(Axis);
    }

    /// <summary>True if both describe the same geometric surface with the same material side.</summary>
    public bool SameGeometry(Surface other, double tolerance)
    {
        if (Kind != other.Kind || Kind == SurfaceKind.Fillet) return false;
        if (Kind == SurfaceKind.Plane)
            return (Axis - other.Axis).Length <= tolerance && Math.Abs(Distance(other.Origin)) <= tolerance;
        return Math.Abs(Radius - other.Radius) <= tolerance
            && Axis.Cross(other.Axis).Length <= tolerance
            && Radial(other.Origin).Length <= tolerance;
    }

    Vector3D Radial(Point3D p)
    {
        var v = p - Origin;
        return v - Axis * v.Dot(Axis);
    }

    static Vector3D AnyPerpendicular(Vector3D v) =>
        (Math.Abs(v.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0)).Cross(v).Normalized();
}
