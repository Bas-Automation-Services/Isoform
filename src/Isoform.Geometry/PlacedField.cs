using BasAutomation.Geometry;

namespace Isoform.Geometry;

/// <summary>
/// A solid moved by a rigid placement, as an assembly places a part: the child is evaluated at the point brought
/// back into the part's own coordinates. A rigid motion keeps distances, so the field stays 1-Lipschitz, and every
/// guarantee of the child (bounds, bit-exact simplification, single patches) carries over through the box that holds
/// the moved box.
/// </summary>
public sealed class PlacedField(Field child, Frame placement) : Field
{
    public Field Child { get; } = child;
    public Frame Placement { get; } = placement;

    public override FieldSample Sample(Point3D p)
    {
        var s = Child.Sample(Placement.ToLocal(p));
        return s with { Gradient = Placement.ToWorld(s.Gradient) };
    }

    public override double Value(Point3D p) => Child.Value(Placement.ToLocal(p));

    public override FieldSample? SampleSurface(int owner, Point3D p) =>
        Child.SampleSurface(owner, Placement.ToLocal(p)) is { } s ? s with { Gradient = Placement.ToWorld(s.Gradient) } : null;

    public override Interval Bounds(Box3D box) => Child.Bounds(LocalBox(box));

    public override Field Simplify(Box3D box) => Child.Simplify(LocalBox(box)) is var c && ReferenceEquals(c, Child) ? this : new PlacedField(c, Placement);

    public override double? SinglePatch(Box3D box) => Child.SinglePatch(LocalBox(box));

    /// <summary>A box in part coordinates holding the whole world box.</summary>
    public Box3D LocalBox(Box3D box)
    {
        var (lo, hi) = (new Point3D(double.MaxValue, double.MaxValue, double.MaxValue), new Point3D(double.MinValue, double.MinValue, double.MinValue));
        foreach (var x in new[] { box.Min.X, box.Max.X })
        foreach (var y in new[] { box.Min.Y, box.Max.Y })
        foreach (var z in new[] { box.Min.Z, box.Max.Z })
        {
            var q = Placement.ToLocal(new Point3D(x, y, z));
            (lo, hi) = (new Point3D(Math.Min(lo.X, q.X), Math.Min(lo.Y, q.Y), Math.Min(lo.Z, q.Z)), new Point3D(Math.Max(hi.X, q.X), Math.Max(hi.Y, q.Y), Math.Max(hi.Z, q.Z)));
        }
        return new Box3D(lo, hi);
    }
}
