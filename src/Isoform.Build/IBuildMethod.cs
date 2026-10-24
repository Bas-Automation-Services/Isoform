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
    /// <summary>The design minus the hulls of the earlier parts: where this section's parts may still go.</summary>
    public Field Remaining => Earlier.Count == 0 ? Design.Field : new MaxField([Design.Field, .. Earlier.Select(p => (Field)new NegateField(p.Hull))]);
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
    /// <summary>A part whose hull is <paramref name="hull"/> (the part itself when null) placed where the part goes.</summary>
    public static BuiltPart Of(string name, PartDocument document, Placement placement, PartDocument? hull = null)
    {
        var compiled = new Compiler(new ModelingSettings()).Compile(hull ?? document);
        var field = compiled.Field ?? throw new InvalidOperationException($"the hull of '{name}' does not build: {compiled.Diagnostics.FirstOrDefault()?.Message}");
        return new(name, document, placement, new PlacedField(field, AssemblyModeler.FrameOf(placement)));
    }
}

/// <summary>The build's own diagnostic codes; the feature id of each is the section's id.</summary>
public static class BuildDiagnostic
{
    /// <summary>A face (or keep-out body) the build document names is not in the design.</summary>
    public const string FaceMissing = "build-face-missing";
    /// <summary>A section gave no part: nothing of the design is left where it is.</summary>
    public const string RegionEmpty = "build-region-empty";
    /// <summary>A tube end meets another tube (a cope): refused.</summary>
    public const string TubeMeetsTube = "build-tube-meets-tube";
    /// <summary>A tube end meets no earlier part and does not leave the design either.</summary>
    public const string OpenEnd = "build-open-end";
}
