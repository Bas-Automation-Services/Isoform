using BasAutomation.Geometry;
using Isoform.Document;

namespace Isoform.Brep.Modeling;

/// <summary>
/// Where a pattern puts a copy: p ↦ Origin + x X + y Y + z Z, a translation, a rotation or a mirror (axes that turn
/// left-handed).
/// </summary>
sealed record Motion(Point3D Origin, Vector3D X, Vector3D Y, Vector3D Z)
{
    public static readonly Motion None = new(new Point3D(0, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0), new Vector3D(0, 0, 1));

    public Point3D Apply(Point3D p) => Origin + X * p.X + Y * p.Y + Z * p.Z;

    public Vector3D Apply(Vector3D v) => X * v.X + Y * v.Y + Z * v.Z;

    public bool Mirrors => X.Cross(Y).Dot(Z) < 0;

    /// <summary>The motion as the F-rep core's placement.</summary>
    public Isoform.Geometry.Frame Frame => new(Origin, X, Y, Z);

    /// <summary>
    /// A region in a frame's XY plane, moved: the frame keeps its moved origin, Z and X. A mirror turns its Y over, so
    /// the region is mirrored in its x axis to stay where the mirror puts it.
    /// </summary>
    public (RigidTransform3D Frame, Region Region) Place(RigidTransform3D frame, Region region)
    {
        var moved = RigidTransform3D.FromFrame(Apply(frame.Origin), Apply(frame.ZAxis), Apply(frame.XAxis));
        return (moved, Mirrors ? new Region(Flipped(region.Outer), [.. region.Holes.Select(Flipped)]) : region);
    }

    static ProfileLoop Flipped(ProfileLoop loop) => new([.. loop.Segments.Select(s => s switch
    {
        LineSegment l => (Segment)new LineSegment(new Point2D(l.From.X, -l.From.Y), new Point2D(l.To.X, -l.To.Y)),
        ArcSegment a => new ArcSegment(new Point2D(a.Center.X, -a.Center.Y), a.Radius, -a.StartAngle, -a.Sweep),
        _ => throw new NotSupportedException(s.GetType().Name),
    })]);

    /// <summary>Copy k of a pattern: the motion applied k times (as the F-rep core places it).</summary>
    public static Motion Of(PatternMotion motion, int k)
    {
        switch (motion)
        {
            case LinearMotion l:
                var step = new Vector3D(l.Direction[0], l.Direction[1], l.Direction[2]).Normalized() * (l.Spacing * k);
                return None with { Origin = new Point3D(step.X, step.Y, step.Z) };
            case CircularMotion c:
                var angle = (c.Angle >= 360 ? c.Angle / c.Count : c.Angle / (c.Count - 1)) * k * Math.PI / 180;
                var (o, axis) = (new Point3D(c.Origin[0], c.Origin[1], c.Origin[2]), new Vector3D(c.Axis[0], c.Axis[1], c.Axis[2]).Normalized());
                Vector3D Turn(Vector3D v) => v * Math.Cos(angle) + axis.Cross(v) * Math.Sin(angle) + axis * (axis.Dot(v) * (1 - Math.Cos(angle)));
                // p ↦ o + R(p − o): origin o − R o, axes R e.
                var shift = Turn(new Vector3D(o.X, o.Y, o.Z));
                return new Motion(new Point3D(o.X - shift.X, o.Y - shift.Y, o.Z - shift.Z), Turn(new(1, 0, 0)), Turn(new(0, 1, 0)), Turn(new(0, 0, 1)));
            case MirrorMotion m:
                var n = new Vector3D(m.Normal[0], m.Normal[1], m.Normal[2]).Normalized();
                var d = n.Dot(new Vector3D(m.Point[0], m.Point[1], m.Point[2]));
                Vector3D Reflect(Vector3D v) => v - n * (2 * n.Dot(v));
                return new Motion(new Point3D(2 * d * n.X, 2 * d * n.Y, 2 * d * n.Z), Reflect(new(1, 0, 0)), Reflect(new(0, 1, 0)), Reflect(new(0, 0, 1)));
            default:
                throw new NotSupportedException(motion.GetType().Name);
        }
    }
}
