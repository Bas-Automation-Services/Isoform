using BasAutomation.Geometry;
using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Build;

/// <summary>
/// One way of making a section of the design (plates, tubes): given the section and what is left of the design once
/// the earlier sections' parts are taken out, it gives the parts, each an exact part document in its own frame.
/// </summary>
public interface IBuildMethod
{
    SectionResult Build(BuildSection section, BuildContext context);
}

/// <summary>What a method works from: the build, the design, and the parts of the sections before this one, in order.</summary>
public sealed record BuildContext(BuildDocument Build, Design Design, IReadOnlyList<BuiltPart> Earlier)
{
    /// <summary>
    /// How far each earlier hull is grown before it is taken out (mm): where a part was cut from the design its hull's
    /// faces lie on the design's own, and the zero-thick skin that would leave is gone.
    /// </summary>
    public const double Contact = 1e-6;

    readonly Lazy<(Field Field, IReadOnlyList<Surface> Surfaces, int[] Parts)> remaining = new(() => Combine(Design, Earlier));

    /// <summary>
    /// The design minus the hulls of the earlier parts (each grown by <see cref="Contact"/>): where this section's parts
    /// may still go. Its owners index <see cref="Surfaces"/>: the design's first, then each earlier hull's, numbered on.
    /// </summary>
    public Field Remaining => remaining.Value.Field;

    /// <summary>The surfaces of <see cref="Remaining"/> by owner id, in the world; a hull's are named <c>part:source</c>.</summary>
    public IReadOnlyList<Surface> Surfaces => remaining.Value.Surfaces;

    /// <summary>The earlier part an owner of <see cref="Remaining"/> belongs to (an index into <see cref="Earlier"/>), or -1 for the design.</summary>
    public int PartOf(int owner) => remaining.Value.Parts[owner];

    static (Field, IReadOnlyList<Surface>, int[]) Combine(Design design, IReadOnlyList<BuiltPart> earlier)
    {
        var surfaces = design.Result.Part.Surfaces.ToList();
        var parts = Enumerable.Repeat(-1, surfaces.Count).ToList();
        if (earlier.Count == 0) return (design.Field, surfaces, [.. parts]);
        var fields = new List<Field> { design.Field };
        for (var i = 0; i < earlier.Count; i++)
        {
            var (part, by) = (earlier[i], surfaces.Count);
            fields.Add(new NegateField(new HullField(part.Hull, by)));
            surfaces.AddRange(part.HullSurfaces.Select(s => s with { Id = s.Id + by, Source = $"{part.Name}:{s.Source}" }));
            parts.AddRange(Enumerable.Repeat(i, part.HullSurfaces.Count));
        }
        return (new MaxField(fields), surfaces, [.. parts]);
    }
}

/// <summary>A section's parts, and what the method has to say (refusals, empty regions).</summary>
public sealed record SectionResult(IReadOnlyList<BuiltPart> Parts, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// One part a section made: a name for its document (<c>foot</c>, <c>leg</c>), the document in the part's own frame,
/// where it goes, and its hull in the world: what it takes from the parts after it (a plate's own solid, a tube's outer
/// solid with the bore filled).
/// </summary>
public sealed record BuiltPart(string Name, PartDocument Document, Placement Placement, Field Hull)
{
    /// <summary>The surfaces of <see cref="Hull"/> by owner id, moved into the world.</summary>
    public IReadOnlyList<Surface> HullSurfaces { get; init; } = [];

    /// <summary>The part's fitted edges: each within the build's tolerance of the design's section, by measure.</summary>
    public IReadOnlyList<FittedEdge> Fits { get; init; } = [];

    /// <summary>A part whose hull is <paramref name="hull"/> (the part itself when null) placed where the part goes.</summary>
    public static BuiltPart Of(string name, PartDocument document, Placement placement, PartDocument? hull = null)
    {
        var compiled = new Compiler(new ModelingSettings()).Compile(hull ?? document);
        var field = compiled.Field ?? throw new InvalidOperationException($"the hull of '{name}' does not build: {compiled.Diagnostics.FirstOrDefault()?.Message}");
        var frame = AssemblyModeler.FrameOf(placement);
        return new(name, document, placement, new PlacedField(field, frame))
        {
            HullSurfaces = [.. compiled.Surfaces.Select(s => s with { Origin = frame.ToWorld(s.Origin), Axis = frame.ToWorld(s.Axis) })],
        };
    }
}

/// <summary>
/// A plate edge that is fitted, not copied: the design face it follows (<c>lobes/2:lobe/blend</c>), how many tangent
/// arcs make it and their largest distance from the design's section, measured both ways.
/// </summary>
public sealed record FittedEdge(string Source, int Arcs, double Error);

/// <summary>The build's own diagnostic codes; the feature id of each is the section's id.</summary>
public static class BuildDiagnostic
{
    /// <summary>A face (or keep-out body) the build document names is not in the design.</summary>
    public const string FaceMissing = "build-face-missing";
    /// <summary>A section gave no part: nothing of the design is left where it is.</summary>
    public const string RegionEmpty = "build-region-empty";
    /// <summary>A tube end meets another tube (a cope with no closed form, round on round): refused.</summary>
    public const string TubeMeetsTube = "build-tube-meets-tube";
    /// <summary>A tube end meets no flat face of an earlier part and does not leave the design either: refused.</summary>
    public const string OpenEnd = "build-open-end";
    /// <summary>A section has no method to build it: a beam section without a beam method.</summary>
    public const string NoMethod = "build-no-method";
    /// <summary>A beam's profile is not in the catalog, or not as lines and arcs.</summary>
    public const string ProfileMissing = "build-profile-missing";
}

/// <summary>
/// A hull grown by <see cref="BuildContext.Contact"/>, its owners numbered on by <paramref name="by"/> so it can share
/// one list of surfaces with the design.
/// </summary>
sealed class HullField(Field child, int by) : Field
{
    const double Grow = BuildContext.Contact;

    public override FieldSample Sample(Point3D p)
    {
        var s = child.Sample(p);
        return s with { Value = s.Value - Grow, Owner = s.Owner + by };
    }

    public override double Value(Point3D p) => child.Value(p) - Grow;

    public override FieldSample? SampleSurface(int owner, Point3D p) =>
        owner >= by && child.SampleSurface(owner - by, p) is { } s ? s with { Value = s.Value - Grow } : null;

    public override Interval Bounds(Box3D box) => child.Bounds(box) is var b ? new(b.Lo - Grow, b.Hi - Grow) : default;
    public override double? SinglePatch(Box3D box) => child.SinglePatch(box);
    public override Field Simplify(Box3D box) => child.Simplify(box) is var c && ReferenceEquals(c, child) ? this : new HullField(c, by);
}
