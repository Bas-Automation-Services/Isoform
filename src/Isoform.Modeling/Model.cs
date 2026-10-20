using Isoform.Document;

namespace Isoform.Modeling;

public sealed record ModelingSettings
{
    /// <summary>Modeling tolerance in mm. Matches OCCT's Precision::Confusion.</summary>
    public double Tolerance { get; init; } = 1e-7;

    /// <summary>
    /// Finest cell size in mm for meshing and topology extraction: the resolution at edges and corners. Walls, gaps
    /// and holes of at least 2 × CellSize are always resolved; smaller ones give a below-resolution diagnostic.
    /// </summary>
    public double CellSize { get; init; } = 0.5;

    /// <summary>Largest distance in mm the mesh may stray from a curved surface away from its edges.</summary>
    public double ChordTolerance { get; init; } = 0.01;
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
    public const string BelowResolution = "below-resolution";
    public const string BackendFailed = "backend-failed";
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
