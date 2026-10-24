using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Build;

/// <summary>
/// Tubes: along the axis of a named cylinder (a tube per copy) or a line. From the middle of the run outwards, each
/// end stops where the axis first leaves what is left of the design: on a flat face of an earlier part (a plate), the
/// end is cut by that face's plane, exact (an ellipse edge where it is oblique); where the axis leaves the design
/// itself, the end is cut square. An end on another tube is refused (a cope, <see cref="BuildDiagnostic.TubeMeetsTube"/>),
/// as is one on a curved face or one that never leaves (<see cref="BuildDiagnostic.OpenEnd"/>). The part is an annulus
/// extruded along z in the tube's own frame (origin where the axis meets end A, x towards end A's tilt) and cut by the
/// two end planes; its hull is the same bar with the bore filled.
/// </summary>
public sealed class TubeMethod : IBuildMethod
{
    static readonly SketchPlane Xy = new([0, 0, 0], [1, 0, 0], [0, 0, 1]);

    public SectionResult Build(BuildSection section, BuildContext context)
    {
        var tube = (TubeSection)section;
        var (parts, diagnostics) = (new List<BuiltPart>(), new List<Diagnostic>());
        var design = context.Design;

        // Each run: its name, a point on the axis, the axis direction and the run's extent along it from that point.
        var runs = new List<(string Name, Point3D Origin, Vector3D Axis, double From, double To)>();
        if (tube.Along is { } along)
        {
            foreach (var (copy, faces) in design.Copies(along))
            {
                if (design.Result.Part.Surfaces.LastOrDefault(s => s.Source == copy) is not { Kind: SurfaceKind.Cylinder } cylinder)
                {
                    diagnostics.Add(new(BuildDiagnostic.FaceMissing, section.Id, $"'{copy}' is not a cylinder for section '{section.Id}' to run along"));
                    continue;
                }
                var axis = cylinder.Axis.Normalized();
                var span = design.PointsOn(faces).Select(p => axis.Dot(p - cylinder.Origin)).ToList();
                runs.Add((copy, cylinder.Origin, axis, span.Min(), span.Max()));
            }
        }
        else if (tube.Line is [var a, var b])
        {
            var (p, q) = (new Point3D(a[0], a[1], a[2]), new Point3D(b[0], b[1], b[2]));
            runs.Add((section.Id, p, (q - p).Normalized(), 0, (q - p).Length));
        }

        var limit = (design.Bounds.Max - design.Bounds.Min).Length;
        foreach (var (name, origin, axis, from, to) in runs)
        {
            var middle = origin + axis * ((from + to) / 2);
            if (context.Remaining.Value(middle) >= 0)
            {
                diagnostics.Add(new(BuildDiagnostic.RegionEmpty, section.Id, $"nothing of the design is left round the middle of '{name}' for section '{section.Id}'"));
                continue;
            }
            var ends = new[] { End(context, middle, -axis, limit), End(context, middle, axis, limit) };
            if (ends.FirstOrDefault(e => e.Refusal is not null) is { Refusal: { } refusal })
            {
                diagnostics.Add(refusal with { FeatureId = section.Id, Message = $"'{name}' of section '{section.Id}': {refusal.Message}" });
                continue;
            }
            var ((atA, normalA, _), (atB, normalB, _)) = (ends[0], ends[1]);

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

    /// <summary>
    /// Where the axis, from <paramref name="start"/> along <paramref name="direction"/>, first leaves what is left of the
    /// design, and the end's outward normal there; or why the tube may not end there.
    /// </summary>
    static (Point3D At, Vector3D Normal, Diagnostic? Refusal) End(BuildContext context, Point3D start, Vector3D direction, double limit)
    {
        var field = context.Remaining;
        // Inside, |f| never exceeds the distance to the boundary: steps of |f| (at least 0.01 mm) stay inside until the
        // last, then bisection finds the crossing.
        var (inside, s) = (0.0, 0.0);
        while (field.Value(start + direction * s) < 0)
        {
            inside = s;
            s += Math.Max(-field.Value(start + direction * s), 0.01);
            if (s > limit) return (start, direction, new(BuildDiagnostic.OpenEnd, null, "an end never leaves the design"));
        }
        var outside = s;
        for (var i = 0; i < 100 && outside - inside > 1e-12; i++)
        {
            var m = (inside + outside) / 2;
            if (field.Value(start + direction * m) < 0) inside = m;
            else outside = m;
        }
        var at = start + direction * outside;
        var owner = field.Sample(at).Owner;
        var part = context.PartOf(owner);
        if (part < 0) return (at, direction, null);
        var earlier = context.Earlier[part];
        var surface = context.Surfaces[owner];
        if (TubeShape.Of(earlier.Document) is not null)
            return (at, direction, new(BuildDiagnostic.TubeMeetsTube, null, $"an end meets the tube '{earlier.Name}' ({surface.Source}): a cope is not made"));
        if (surface.Kind != SurfaceKind.Plane)
            return (at, direction, new(BuildDiagnostic.OpenEnd, null, $"an end meets {surface.Source}, a {surface.Kind.ToString().ToLowerInvariant()} face of '{earlier.Name}', not a flat one"));
        // The hull's face as what is left has it: its gradient points out of the tube, into the part. The end is where
        // the axis meets that plane exactly (the face itself, not the hull grown by the contact).
        var plane = field.SampleSurface(owner, at)!.Value;
        var normal = plane.Gradient.Normalized();
        return (at - direction * ((plane.Value - BuildContext.Contact) / plane.Gradient.Dot(direction)), normal, null);
    }
}
