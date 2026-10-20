namespace Isoform.Document;

/// <summary>A rigid placement: origin, x direction and normal (z); y = normal × x.</summary>
public sealed record Placement(double[] Origin, double[] X, double[] Normal)
{
    public static Placement Identity => new([0, 0, 0], [1, 0, 0], [0, 0, 1]);
}

/// <summary>
/// One occurrence in an assembly. <paramref name="Part"/> names a part or assembly document (a path the caller
/// resolves). <paramref name="Hash"/>, when given, pins the document's content hash, so a changed part is noticed.
/// The id is stable: occurrence names are built from it (<c>left/plate/z+</c>), never from list position.
/// </summary>
/// <remarks>
/// With mates the placement is where solving starts; a <paramref name="Grounded"/> component stays put (the first
/// one, if none is).
/// <para><paramref name="Parameters"/> sets some of the part's parameters for this occurrence (name → expression,
/// as in <see cref="PartDocument.Parameters"/>): one bolt document, several lengths. The pinned hash is still the
/// stored part's, before these are applied.</para>
/// </remarks>
public sealed record Component(string Id, string Part, Placement Placement, string? Hash = null, bool Grounded = false,
    IReadOnlyDictionary<string, string>? Parameters = null);

/// <summary>
/// How a mate holds two faces: <see cref="Mate"/> puts flat faces against each other (normals opposite, gap
/// <c>offset</c>), <see cref="Flush"/> lines them up facing the same way, <see cref="Axis"/> makes the axes of two
/// cylinders or cones one line.
/// </summary>
public enum MateKind { Mate, Flush, Axis }

/// <summary>A mate between two faces named by occurrence, e.g. <c>pin/pin/shank</c> and <c>lower/bore/wall</c>.</summary>
public sealed record Mate(string Id, MateKind Kind, string A, string B, double Offset = 0);

/// <summary>An assembly: placed occurrences of parts and sub-assemblies, and mates between their faces.</summary>
public sealed record AssemblyDocument(IReadOnlyList<Component> Components, IReadOnlyList<Mate>? Mates = null)
{
    public const string FormatVersion = "isoform-assembly/0";
}

/// <summary>An assembly with the documents it uses in one file, for sharing and for the playground.</summary>
public sealed record Bundle(AssemblyDocument Assembly, IReadOnlyDictionary<string, string> Documents)
{
    public const string FormatVersion = "isoform-bundle/0";
}
