namespace Isoform.Document;

/// <summary>
/// How a design is made from plates, tubes and beams: sections bound to the design by face name, in priority order (each
/// section is cut by the parts of the sections before it), with the build's material, tolerance, keep-outs and
/// interfaces. <paramref name="Design"/> names the design part (a path the caller resolves, as a component's part).
/// </summary>
/// <remarks>
/// A face name also names its pattern copies (<c>leg/a</c> covers <c>legs/1:leg/a</c>…); a copy's own name picks
/// that copy. Nothing here chooses sections or sizes members: the document says what is assigned, the build methods do
/// the rest.
/// </remarks>
public sealed record BuildDocument(string Design, IReadOnlyList<BuildSection> Sections, double Tolerance = BuildDocument.DefaultTolerance,
    Material? Material = null, IReadOnlyList<Keepout>? Keepouts = null, IReadOnlyList<string>? Interfaces = null)
{
    public const string FormatVersion = "isoform-build/0";

    /// <summary>How far a fitted plate edge may stray from the design's section, in mm, either side.</summary>
    public const double DefaultTolerance = 0.05;
}

/// <summary>A material by name, with its density in kg/mm³ (steel: 7.85e-6).</summary>
public sealed record Material(string Name, double Density);

/// <summary>A body of the design no part may come within <paramref name="Clearance"/> mm of.</summary>
public sealed record Keepout(string Body, double Clearance = 0);

/// <summary>One section of the build: an id (stable, used in edit paths and part names) and a build method.</summary>
public abstract record BuildSection(string Id);

/// <summary>
/// Plates of <paramref name="Thickness"/>: on the named flat <paramref name="Face"/> (the plate's outer face on it,
/// thickness into the material), or on a <paramref name="Plane"/> (thickness along its normal). Every region of the
/// section is a plate; <paramref name="Near"/> takes the one nearest a point.
/// </summary>
public sealed record PlateSection(string Id, double Thickness, string? Face = null, SketchPlane? Plane = null, double[]? Near = null) : BuildSection(Id);

/// <summary>
/// Tubes of <paramref name="Outside"/> diameter and <paramref name="Wall"/> thickness: along the axis of the named
/// cylinder <paramref name="Along"/> (and its pattern copies, a tube each), or along a <paramref name="Line"/> of two points.
/// </summary>
public sealed record TubeSection(string Id, double Outside, double Wall, string? Along = null, double[][]? Line = null) : BuildSection(Id);

/// <summary>
/// Beams of a catalog profile by its id (<c>IPE200</c>, <c>RHS100x50x4</c>): along the axis of the named cylinder
/// <paramref name="Along"/> (a beam per copy), or along each of <paramref name="Lines"/> (a beam per line, the joints
/// where lines meet made by the beam method). <paramref name="Up"/> is the direction the section's y points (null: the
/// method's default); <paramref name="Anchor"/> the point of the section's box on the line, one of
/// <see cref="Anchors"/> (null: <c>center</c>). The profile is resolved by the build's beam method, from its catalog.
/// </summary>
public sealed record BeamSection(string Id, string Profile, string? Along = null, double[][][]? Lines = null, double[]? Up = null, string? Anchor = null) : BuildSection(Id)
{
    public static readonly string[] Anchors = ["center", "top", "bottom", "left", "right", "top-left", "top-right", "bottom-left", "bottom-right"];
}
