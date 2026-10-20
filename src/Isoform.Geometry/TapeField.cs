using BasAutomation.Geometry;

namespace Isoform.Geometry;

/// <summary>
/// A field evaluated from its compiled <see cref="Tape"/> instead of walking the tree. Value, sample, bounds, single
/// patch and per-surface samples are bit-identical to the tree's, and simplifying gives a shorter tape making the
/// tree's own decisions (down to a single leaf or profile, where it hands back the tree's own object), so the mesher
/// and queries see exactly the same field, only faster.
/// </summary>
public sealed class TapeField : Field
{
    TapeField(Tape tape) => Tape = tape;

    /// <summary>Compiles <paramref name="field"/>, or returns it when it already is a tape.</summary>
    public static TapeField Compile(Field field) => field as TapeField ?? new(Tape.Compile(field));

    public Tape Tape { get; }

    public override FieldSample Sample(Point3D p) => Tape.Sample(p);

    public override double Value(Point3D p) => Tape.Value(p);

    public override Interval Bounds(Box3D box) => Tape.Bounds(box);

    /// <summary>The simplified tape; the tree's own field instead when that is all the tape still holds.</summary>
    public override Field Simplify(Box3D box)
    {
        var t = Tape.Simplify(box);
        return t.Single ?? (t == Tape ? this : new TapeField(t));
    }

    public override FieldSample? SampleSurface(int owner, Point3D p) => Tape.SampleSurface(owner, p);

    public override double? SinglePatch(Box3D box) => Tape.SinglePatch(box);

    /// <summary>Values at many points at once; see <see cref="Tape.Values"/>.</summary>
    public void Values(ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, Span<double> values) => Tape.Values(x, y, z, values);
}
