using Isoform.Document;

namespace Isoform.Modeling;

public sealed record ModelingSettings
{
    /// <summary>Modeling tolerance in mm. Matches OCCT's Precision::Confusion.</summary>
    public double Tolerance { get; init; } = 1e-7;

    /// <summary>
    /// Finest cell size in mm for meshing and topology extraction: the resolution at edges and corners. Walls, gaps
    /// and holes of at least 2 × CellSize are always resolved; smaller ones are refined locally (see
    /// <see cref="Refinement"/>), and what is still smaller gives a below-resolution diagnostic.
    /// </summary>
    public double CellSize { get; init; } = 0.5;

    /// <summary>
    /// How many times the cells may be halved locally: where a feature is below the resolution it is meshed again with
    /// cells small enough for it, down to CellSize / 2^Refinement, and only what is smaller is reported. 0: never.
    /// </summary>
    public int Refinement { get; init; } = 3;

    /// <summary>Largest distance in mm the mesh may stray from a curved surface away from its edges.</summary>
    public double ChordTolerance { get; init; } = 0.01;

    /// <summary>
    /// Put the cell lattice on a fixed world grid instead of the part's corner, so an edit that moves the bounding
    /// box keeps the cached regions it did not touch. Another lattice gives another (equally valid) mesh.
    /// </summary>
    public bool SnapGrid { get; init; }
}

/// <summary>Something the user or agent must know about: invalid input, degenerate geometry, a feature that failed.</summary>
public sealed record Diagnostic(string Code, string? FeatureId, string Message)
{
    public const string InvalidParameter = "invalid-parameter";
    public const string TangentContact = "tangent-contact";
    public const string NoEffect = "no-effect";
    public const string MultipleBodies = "multiple-bodies";
    public const string FilletInvalid = "fillet-invalid";
    public const string UnsupportedFillet = "unsupported-fillet";
    public const string ChamferInvalid = "chamfer-invalid";
    public const string UnsupportedChamfer = "unsupported-chamfer";
    /// <summary>Three fillets of unequal radii meet at a corner: no exact corner patch, so they meet in a mitre (a warning).</summary>
    public const string CornerMitred = "corner-mitred";
    public const string BelowResolution = "below-resolution";
    public const string BackendFailed = "backend-failed";
    public const string PartMissing = "part-missing";
    public const string PartChanged = "part-changed";
    public const string MatesConflict = "mates-conflict";
    /// <summary>A sketch's constraints cannot all hold; the message names them.</summary>
    public const string SketchConflict = "sketch-conflict";
    /// <summary>A sketch constraint the others already imply (a warning).</summary>
    public const string SketchRedundant = "sketch-redundant";
    /// <summary>A sketch that cannot be used: bad references, loops that cross or branch, a region that is not there.</summary>
    public const string SketchInvalid = "sketch-invalid";
    /// <summary>A sketch placed on a face that is not a flat face of the part where the sketch stands.</summary>
    public const string SketchPlaneLost = "sketch-plane-lost";
    /// <summary>A sketch entity projected from a part edge whose faces are gone or ambiguous, or that projects to an ellipse now.</summary>
    public const string SketchReferenceLost = "sketch-reference-lost";
}

/// <summary>A connected face that exists after regeneration, with every source surface it came from.</summary>
public sealed record TopoFace(int Index, IReadOnlyList<string> Sources, int Body);

public sealed record TopoEdge(int Index, int FaceA, int FaceB);

public sealed record TopoVertex(int Index, IReadOnlyList<int> Faces);

/// <summary>Backend-neutral result topology. Rebuilt on every regeneration; never stored.</summary>
public sealed class Topology(IReadOnlyList<TopoFace> faces, IReadOnlyList<TopoEdge> edges, IReadOnlyList<TopoVertex> vertices, int bodyCount)
{
    public IReadOnlyList<TopoFace> Faces { get; } = faces;
    public IReadOnlyList<TopoEdge> Edges { get; } = edges;
    public IReadOnlyList<TopoVertex> Vertices { get; } = vertices;
    public int BodyCount { get; } = bodyCount;

    public IEnumerable<int> Neighbors(int face) =>
        Edges.Where(e => e.FaceA == face || e.FaceB == face).Select(e => e.FaceA == face ? e.FaceB : e.FaceA).Distinct();

    /// <summary>Sorted sources of all neighbouring faces: what a piece reference remembers about its face.</summary>
    public string[] Signature(int face) =>
        Neighbors(face).SelectMany(n => Faces[n].Sources).Distinct().Order(StringComparer.Ordinal).ToArray();
}

public abstract class ModelResult(IReadOnlyList<Diagnostic> diagnostics, Topology? topology)
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;

    /// <summary>Null when the document could not produce geometry at all.</summary>
    public Topology? Topology { get; } = topology;

    /// <summary>The face carrying <paramref name="source"/> closest to a picked point, as a ray hit would give.</summary>
    public abstract int? FaceNear(string source, double x, double y, double z);
}

public interface IModeler
{
    ModelResult Regenerate(PartDocument document);
}
