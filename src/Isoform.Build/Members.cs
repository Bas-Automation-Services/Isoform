using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Build;

/// <summary>A run of a member: its name (the copy of a face, or the section), a point on its axis, the axis, and the run's extent along it from that point.</summary>
public sealed record MemberRun(string Name, Point3D Origin, Vector3D Axis, double From, double To)
{
    public Point3D Middle => Origin + Axis * ((From + To) / 2);
}

/// <summary>Where a member's end stops: the point on its axis and the end plane's outward normal; or why it may not end there.</summary>
public readonly record struct MemberEnd(Point3D At, Vector3D Normal, Diagnostic? Refusal);

/// <summary>
/// What the methods that make members (tubes, beams) share: their runs, along the axis of a named cylinder (a run per
/// copy) or along lines; their ends, from the middle of the run outwards to where the axis first leaves what is left
/// of the design; and a member's part document, a section extruded along the axis and cut by its end planes.
/// </summary>
public static class Members
{
    /// <summary>The runs of section <paramref name="section"/>: along each copy of the cylinder <paramref name="along"/>, or each of <paramref name="lines"/>.</summary>
    public static IReadOnlyList<MemberRun> Runs(BuildContext context, string section, string? along, IEnumerable<double[][]> lines, List<Diagnostic> diagnostics)
    {
        var (design, runs) = (context.Design, new List<MemberRun>());
        if (along is not null)
        {
            foreach (var (copy, faces) in design.Copies(along))
            {
                if (design.Result.Part.Surfaces.LastOrDefault(s => s.Source == copy) is not { Kind: SurfaceKind.Cylinder } cylinder)
                {
                    diagnostics.Add(new(BuildDiagnostic.FaceMissing, section, $"'{copy}' is not a cylinder for section '{section}' to run along"));
                    continue;
                }
                var axis = cylinder.Axis.Normalized();
                var span = design.PointsOn(faces).Select(p => axis.Dot(p - cylinder.Origin)).ToList();
                runs.Add(new(copy, cylinder.Origin, axis, span.Min(), span.Max()));
            }
        }
        foreach (var line in lines)
        {
            var (p, q) = (new Point3D(line[0][0], line[0][1], line[0][2]), new Point3D(line[1][0], line[1][1], line[1][2]));
            runs.Add(new(section, p, (q - p).Normalized(), 0, (q - p).Length));
        }
        return runs;
    }

    /// <summary>
    /// A run's two ends, from its middle outwards: A against the axis, B along it; or null, with the reason added to
    /// <paramref name="diagnostics"/>, when nothing is left round the middle or an end is refused.
    /// </summary>
    public static (MemberEnd A, MemberEnd B)? Ends(BuildContext context, string section, MemberRun run, List<Diagnostic> diagnostics)
    {
        var middle = run.Middle;
        if (context.Remaining.Value(middle) >= 0)
        {
            diagnostics.Add(new(BuildDiagnostic.RegionEmpty, section, $"nothing of the design is left round the middle of '{run.Name}' for section '{section}'"));
            return null;
        }
        MemberEnd[] ends = [End(context, middle, -run.Axis), End(context, middle, run.Axis)];
        if (ends.FirstOrDefault(e => e.Refusal is not null) is { Refusal: { } refusal })
        {
            diagnostics.Add(refusal with { FeatureId = section, Message = $"'{run.Name}' of section '{section}': {refusal.Message}" });
            return null;
        }
        return (ends[0], ends[1]);
    }

    /// <summary>
    /// Where the axis, from <paramref name="start"/> along <paramref name="direction"/>, first leaves what is left of the
    /// design, and the end's outward normal there: on a flat face of an earlier part, that face's plane (exactly); where
    /// it leaves the design itself, square. Refused on an earlier tube (a cope, <see cref="BuildDiagnostic.TubeMeetsTube"/>),
    /// on a curved face, or when it never leaves (<see cref="BuildDiagnostic.OpenEnd"/>).
    /// </summary>
    public static MemberEnd End(BuildContext context, Point3D start, Vector3D direction)
    {
        var field = context.Remaining;
        var limit = (context.Design.Bounds.Max - context.Design.Bounds.Min).Length;
        // Inside, |f| never exceeds the distance to the boundary: steps of |f| (at least 0.01 mm) stay inside until the
        // last, then bisection finds the crossing.
        var (inside, s) = (0.0, 0.0);
        while (field.Value(start + direction * s) < 0)
        {
            inside = s;
            s += Math.Max(-field.Value(start + direction * s), 0.01);
            if (s > limit) return new(start, direction, new(BuildDiagnostic.OpenEnd, null, "an end never leaves the design"));
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
        if (part < 0) return new(at, direction, null);
        var earlier = context.Earlier[part];
        var surface = context.Surfaces[owner];
        if (TubeShape.Of(earlier.Document) is not null)
            return new(at, direction, new(BuildDiagnostic.TubeMeetsTube, null, $"an end meets the tube '{earlier.Name}' ({surface.Source}): a cope is not made"));
        if (surface.Kind != SurfaceKind.Plane)
            return new(at, direction, new(BuildDiagnostic.OpenEnd, null, $"an end meets {surface.Source}, a {surface.Kind.ToString().ToLowerInvariant()} face of '{earlier.Name}', not a flat one"));
        // The hull's face as what is left has it: its gradient points out of the member, into the part. The end is where
        // the axis meets that plane exactly (the face itself, not the hull grown by the contact).
        var plane = field.SampleSurface(owner, at)!.Value;
        var normal = plane.Gradient.Normalized();
        return new(at - direction * ((plane.Value - BuildContext.Contact) / plane.Gradient.Dot(direction)), normal, null);
    }

    /// <summary>
    /// A member's part document in its own frame (the profile's): the section (without its holes when not
    /// <paramref name="bored"/>, the hull) extruded along z past both ends, cut by end A's plane through the origin and
    /// end B's through (0, 0, <paramref name="length"/>), each given by its outward normal, then by <paramref name="cuts"/>.
    /// </summary>
    public static PartDocument Document(CatalogProfile profile, double length, Vector3D endA, Vector3D endB, IReadOnlyList<Feature>? cuts = null, bool bored = true)
    {
        var box = profile.Box;
        var r = Math.Sqrt(Math.Pow(Math.Max(-box.MinX, box.MaxX), 2) + Math.Pow(Math.Max(-box.MinY, box.MaxY), 2));
        double Reach(Vector3D n) => r * Math.Sqrt(n.X * n.X + n.Y * n.Y) / Math.Abs(n.Z) + 1;
        return new(
        [
            profile.Sketch("section", bored),
            new Extrude("beam", BooleanOp.New, null, null, -Reach(endA), length + Reach(endB)) { Sketch = "section" },
            new PlaneCut("end-a", [0, 0, 0], [endA.X, endA.Y, endA.Z]),
            new PlaneCut("end-b", [0, 0, length], [endB.X, endB.Y, endB.Z]),
            .. cuts ?? [],
        ]);
    }
}
