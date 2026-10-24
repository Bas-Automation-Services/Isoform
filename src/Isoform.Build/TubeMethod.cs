using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Build;

/// <summary>
/// Tubes: along the axis of a named cylinder (a tube per copy) or a line. From the middle of the run outwards, each
/// end stops where the axis first leaves what is left of the design (<see cref="Members.End"/>): on a flat face of an
/// earlier part (a plate), the end is cut by that face's plane, exact (an ellipse edge where it is oblique); where the
/// axis leaves the design itself, the end is cut square. An end on another tube is refused (a cope,
/// <see cref="BuildDiagnostic.TubeMeetsTube"/>), as is one on a curved face or one that never leaves
/// (<see cref="BuildDiagnostic.OpenEnd"/>). The part is an annulus extruded along z in the tube's own frame (origin
/// where the axis meets end A, x towards end A's tilt) and cut by the two end planes; its hull is the same bar with the
/// bore filled.
/// </summary>
public sealed class TubeMethod : IBuildMethod
{
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);

    public SectionResult Build(BuildSection section, BuildContext context)
    {
        var tube = (TubeSection)section;
        var (parts, diagnostics) = (new List<BuiltPart>(), new List<Diagnostic>());
        foreach (var run in Members.Runs(context, section.Id, tube.Along, tube.Line is { } line ? [line] : [], diagnostics))
        {
            if (Members.Ends(context, section.Id, run, diagnostics) is not { } ends) continue;
            var ((atA, normalA, _), (atB, normalB, _), axis) = (ends.A, ends.B, run.Axis);

            // The tube's frame: z along the axis from end A, x the way end A leans (any square direction when it does not).
            var lean = normalA - axis * axis.Dot(normalA);
            if (lean.Length < 1e-9) lean = (normalB - axis * axis.Dot(normalB)) * -1;
            if (lean.Length < 1e-9) lean = new Vector3D(1, 0, 0) - axis * axis.X;
            if (lean.Length < 0.1) lean = new Vector3D(0, 1, 0) - axis * axis.Y;
            var frame = Frame.FromXAndNormal(atA, lean.Normalized(), axis);
            var length = axis.Dot(atB - atA);
            var (endA, endB) = (frame.ToLocal(normalA), frame.ToLocal(normalB));
            var placement = new Placement([atA.X, atA.Y, atA.Z], [frame.X.X, frame.X.Y, frame.X.Z], [axis.X, axis.Y, axis.Z]);
            parts.Add(BuiltPart.Of(section.Id, Document(tube.Outside, tube.Wall, length, endA, endB), placement,
                hull: Document(tube.Outside, tube.Wall, length, endA, endB, bored: false)));
        }
        return new SectionResult(parts, diagnostics);
    }

    /// <summary>
    /// A tube's part document in its own frame: the annulus (or, not <paramref name="bored"/>, the disc) extruded along z
    /// past both ends, cut by end A's plane through the origin and end B's through (0, 0, <paramref name="length"/>), each
    /// given by its outward normal.
    /// </summary>
    public static PartDocument Document(double outside, double wall, double length, Vector3D endA, Vector3D endB, bool bored = true)
    {
        var r = outside / 2;
        double Reach(Vector3D n) => r * Math.Sqrt(n.X * n.X + n.Y * n.Y) / Math.Abs(n.Z) + 1;
        return new(
        [
            new Sketch("section", new SketchPlacement(Xy), new Dictionary<string, double[]> { ["c"] = [0, 0] },
                bored ? [new SketchCircle("outside", "c", r), new SketchCircle("bore", "c", r - wall)] : [new SketchCircle("outside", "c", r)], []),
            new Extrude("tube", BooleanOp.New, null, null, -Reach(endA), length + Reach(endB)) { Sketch = "section" },
            new PlaneCut("end-a", [0, 0, 0], [endA.X, endA.Y, endA.Z]),
            new PlaneCut("end-b", [0, 0, length], [endB.X, endB.Y, endB.Z]),
        ]);
    }
}
