using System.Collections.ObjectModel;

namespace Isoform.Document;

/// <summary>How a feature's solid combines with the part so far.</summary>
public enum BooleanOp { New, Join, Cut }

/// <summary>
/// A node in the feature graph. Ids are assigned at creation and never derived from list position,
/// so diffs and merges stay stable when features are inserted or reordered.
/// </summary>
public abstract record Feature(string Id)
{
    /// <summary>
    /// The body this feature works on. A feature with op <c>new</c> starts a body under this name (or its own id);
    /// any other feature that names a body makes it the current one. Without it a feature works on the current
    /// body: the one last started or named. Fillets and chamfers find their body from their faces.
    /// </summary>
    public string? Body { get; init; }
}

/// <summary>Axis-aligned block from <paramref name="Origin"/> with <paramref name="Size"/> (W, L, H) in mm.</summary>
/// <remarks>A <paramref name="Blend"/> above 0 joins or cuts smoothly: the faces merge through an <c>id/blend</c> face about that wide.</remarks>
public sealed record Block(string Id, BooleanOp Op, double[] Origin, double[] Size, double Blend = 0) : Feature(Id);

/// <summary>Through-all hole along Z at <paramref name="Center"/> (x, y).</summary>
public sealed record Hole(string Id, double[] Center, double Diameter) : Feature(Id);

/// <summary>Removes all material on the side <paramref name="Normal"/> points to, starting at <paramref name="Point"/>.</summary>
public sealed record PlaneCut(string Id, double[] Point, double[] Normal) : Feature(Id);

/// <summary>
/// Constant-radius fillet on the edge between two source faces, e.g. ["plate/z+", "plate/x+"], or with
/// <paramref name="Loop"/> on every edge round one face (its edge list then stays empty). A loop fillet names its
/// faces after the face on the other side: <c>round/rib/a</c>.
/// </summary>
public sealed record Fillet(string Id, string[] Edge, double Radius, string? Loop = null) : Feature(Id);

/// <summary>Equal-distance chamfer on the edge between two source faces: a plane, or a cone on a hole or boss edge.</summary>
public sealed record Chamfer(string Id, string[] Edge, double Distance, string? Loop = null) : Feature(Id);

/// <summary>Hollows the solid into a closed skin of wall <paramref name="Thickness"/>; inner faces are named after their outer face.</summary>
public sealed record Shell(string Id, double Thickness) : Feature(Id);

/// <summary>A sketch plane: origin, in-plane x direction and normal. Its y direction is normal × x.</summary>
public sealed record SketchPlane(double[] Origin, double[] X, double[] Normal);

/// <summary>
/// One edge of a profile, from the previous point to <paramref name="To"/>. A non-zero <paramref name="Bulge"/>
/// makes it an arc: tan(θ/4) for an included angle θ, positive turning left (counter-clockwise).
/// </summary>
public sealed record ProfileEdge(string Id, double[] To, double Bulge = 0);

/// <summary>A closed loop of lines and arcs in sketch coordinates; the last edge ends at <paramref name="Start"/>.</summary>
public sealed record Profile(double[] Start, IReadOnlyList<ProfileEdge> Edges);

/// <summary>
/// A profile swept along the sketch normal from <paramref name="Start"/> to <paramref name="End"/>. Side faces are
/// named after their profile edge (<c>rib/top</c>), the caps <c>rib/start</c> and <c>rib/end</c>. With
/// <see cref="Sketch"/> the regions of that sketch are swept instead of <paramref name="Profile"/>, which is then null
/// as is <paramref name="Plane"/>; side faces are named after the sketch's entities.
/// </summary>
public sealed record Extrude(string Id, BooleanOp Op, SketchPlane? Plane, Profile? Profile, double Start, double End, double Blend = 0) : Feature(Id)
{
    /// <summary>The id of an earlier <see cref="Isoform.Document.Sketch"/> to sweep, instead of an inline profile.</summary>
    public string? Sketch { get; init; }

    /// <summary>Which of the sketch's regions, each named by entities of its outer loop; null for every region.</summary>
    public IReadOnlyList<string[]>? Regions { get; init; }
}

/// <summary>
/// A profile revolved round the sketch's y axis: profile x is the distance from the axis (never negative), y the
/// height along it. <paramref name="Angle"/> in degrees, right-handed round the axis from the sketch plane; below
/// 360 the two flat ends are <c>id/start</c> and <c>id/end</c>. With <see cref="Sketch"/>, as for an extrude.
/// </summary>
public sealed record Revolve(string Id, BooleanOp Op, SketchPlane? Plane, Profile? Profile, double Angle = 360, double Blend = 0) : Feature(Id)
{
    /// <inheritdoc cref="Extrude.Sketch"/>
    public string? Sketch { get; init; }

    /// <inheritdoc cref="Extrude.Regions"/>
    public IReadOnlyList<string[]>? Regions { get; init; }
}

/// <summary>Where a sketch lies: a plane, or a flat face of the part by name (it then follows the face).</summary>
public sealed record SketchPlacement(SketchPlane? Plane = null, string? Face = null);

/// <summary>A line, arc or circle of a sketch, between named points. Construction geometry is never swept.</summary>
public abstract record SketchEntity(string Id)
{
    public bool Construction { get; init; }
}

/// <summary>A straight line from point <paramref name="From"/> to point <paramref name="To"/>.</summary>
public sealed record SketchLine(string Id, string From, string To) : SketchEntity(Id);

/// <summary>An arc round <paramref name="Center"/>, counter-clockwise from <paramref name="From"/> to <paramref name="To"/>.</summary>
public sealed record SketchArc(string Id, string Center, string From, string To) : SketchEntity(Id);

/// <summary>A full circle round <paramref name="Center"/>; its radius is solved like a point's coordinates.</summary>
public sealed record SketchCircle(string Id, string Center, double Radius) : SketchEntity(Id);

/// <summary>
/// A constraint: its kind, the points or entities it binds and, for a dimension, its value (mm, or degrees for an
/// angle). Kinds: coincident, horizontal, vertical, parallel, perpendicular, tangent, equal, midpoint, symmetric, on,
/// fix; dimensions length, distance, horizontal-distance, vertical-distance, radius, diameter, angle.
/// </summary>
public sealed record SketchConstraint(string Kind, string[] Refs, double? Value = null)
{
    public static readonly string[] Geometric = ["coincident", "horizontal", "vertical", "parallel", "perpendicular", "tangent", "equal", "midpoint", "symmetric", "on", "fix"];
    public static readonly string[] Dimensions = ["length", "distance", "horizontal-distance", "vertical-distance", "radius", "diameter", "angle"];
}

/// <summary>
/// A 2D sketch: named points with their last solved coordinates, the entities between them, and constraints. It
/// makes no solid of its own; extrudes and revolves sweep its regions. The points carry the solution, so a document
/// regenerates the same every time and diffs as geometry; the constraints carry the intent.
/// </summary>
public sealed record Sketch(string Id, SketchPlacement On, IReadOnlyDictionary<string, double[]> Points,
    IReadOnlyList<SketchEntity> Entities, IReadOnlyList<SketchConstraint> Constraints) : Feature(Id);

/// <summary>
/// Moves every face of the part so far by <paramref name="Distance"/> along its outward normal (negative: inward).
/// Faces keep their names. Flat edges stay sharp; grown convex corners of swept profiles become round.
/// </summary>
public sealed record Offset(string Id, double Distance) : Feature(Id);

/// <summary>Where a lattice goes: an axis-aligned box, or the solid of an earlier block, extrude or revolve (by id).</summary>
public sealed record LatticeRegion(string? Feature = null, double[]? Origin = null, double[]? Size = null);

/// <summary>
/// Fills the body with a periodic lattice of cell <paramref name="Cell"/> mm, on the world grid: TPMS sheets
/// (<c>gyroid</c>, <c>schwarz-p</c>, <c>diamond</c>) at least <paramref name="Thickness"/> thick, or struts of that
/// diameter (<c>bcc</c>, <c>octet</c>, <c>cubic</c>). With a <paramref name="Skin"/> above 0 the body keeps a closed
/// wall that thick (faces <c>id/inner:&lt;face&gt;</c>, as a shell names them) and the lattice fills the cavity, joined
/// to it. A <paramref name="Region"/> limits the lattice; the body stays solid outside it (faces <c>id/region:…</c>).
/// The lattice is one face, <c>id/surface</c> for sheets and <c>id/struts</c> for struts.
/// </summary>
public sealed record Lattice(string Id, string Kind, double Cell, double Thickness, double Skin = 0, LatticeRegion? Region = null) : Feature(Id)
{
    public static readonly string[] Kinds = ["gyroid", "schwarz-p", "diamond", "bcc", "octet", "cubic"];

    /// <summary>Sheets (triply periodic minimal surfaces) rather than struts.</summary>
    public bool Sheets => Kind is "gyroid" or "schwarz-p" or "diamond";

    /// <summary>The name of the lattice's face.</summary>
    public string Face => $"{Id}/{(Sheets ? "surface" : "struts")}";
}

/// <summary>How a pattern places its copies.</summary>
public abstract record PatternMotion;

/// <summary><paramref name="Count"/> instances in all, <paramref name="Spacing"/> mm apart along <paramref name="Direction"/>.</summary>
public sealed record LinearMotion(double[] Direction, double Spacing, int Count) : PatternMotion;

/// <summary>
/// <paramref name="Count"/> instances in all round an axis. A full 360° spreads them evenly; a smaller
/// <paramref name="Angle"/> puts the last one at that angle.
/// </summary>
public sealed record CircularMotion(double[] Origin, double[] Axis, int Count, double Angle = 360) : PatternMotion;

/// <summary>One mirrored copy in the plane through <paramref name="Point"/> with <paramref name="Normal"/>.</summary>
public sealed record MirrorMotion(double[] Point, double[] Normal) : PatternMotion;

/// <summary>
/// Copies of earlier features (blocks, holes, plane cuts, extrudes, revolves), applied at the pattern's place in the
/// history. Copy k of source <c>hole/wall</c> is named <c>id/k:hole/wall</c>, so copies keep their names when the
/// count grows.
/// </summary>
public sealed record Pattern(string Id, string[] Features, PatternMotion Motion) : Feature(Id);

/// <summary>
/// Joins <paramref name="Tool"/> into the current body (or <see cref="Feature.Body"/>), or cuts it out of it. The tool
/// body is used up unless <paramref name="Keep"/>; its faces keep their names.
/// </summary>
public sealed record CombineBodies(string Id, string Tool, BooleanOp Op, bool Keep = false) : Feature(Id);

/// <summary>The stored model: the only thing that is versioned. Geometry is always regenerated from it.</summary>
public sealed record PartDocument(IReadOnlyList<Feature> Features)
{
    public const string FormatVersion = "isoform/0";
    public const string Units = "mm";

    /// <summary>
    /// Named parameters, each an expression over the others: <c>"100"</c>, <c>"width / 2"</c> (see
    /// <see cref="Expression"/>). The text keeps them sorted by name, a constant as a number and any other expression
    /// as a string starting with '=': <c>"depth": "=width / 2"</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>
    /// Expressions in feature fields, by edit path and without the '=': <c>"hole.diameter"</c> → <c>"width / 2"</c>,
    /// <c>"rib.profile.edges.1.to.0"</c> → <c>"depth"</c>. The expression is the design intent, so the text and the
    /// hash keep it; the typed field under such a path only holds a placeholder. <see cref="Parametric.Resolve"/>
    /// gives the values.
    /// </summary>
    public IReadOnlyDictionary<string, string> Expressions { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    public Feature this[string id] => Features.Single(f => f.Id == id);
}
