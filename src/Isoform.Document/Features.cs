namespace Isoform.Document;

/// <summary>How a feature's solid combines with the part so far.</summary>
public enum BooleanOp { New, Join, Cut }

/// <summary>
/// A node in the feature graph. Ids are assigned at creation and never derived from list position,
/// so diffs and merges stay stable when features are inserted or reordered.
/// </summary>
public abstract record Feature(string Id);

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
/// named after their profile edge (<c>rib/top</c>), the caps <c>rib/start</c> and <c>rib/end</c>.
/// </summary>
public sealed record Extrude(string Id, BooleanOp Op, SketchPlane Plane, Profile Profile, double Start, double End, double Blend = 0) : Feature(Id);

/// <summary>
/// A profile revolved round the sketch's y axis: profile x is the distance from the axis (never negative), y the
/// height along it. <paramref name="Angle"/> in degrees, right-handed round the axis from the sketch plane; below
/// 360 the two flat ends are <c>id/start</c> and <c>id/end</c>.
/// </summary>
public sealed record Revolve(string Id, BooleanOp Op, SketchPlane Plane, Profile Profile, double Angle = 360, double Blend = 0) : Feature(Id);

/// <summary>
/// Moves every face of the part so far by <paramref name="Distance"/> along its outward normal (negative: inward).
/// Faces keep their names. Flat edges stay sharp; grown convex corners of swept profiles become round.
/// </summary>
public sealed record Offset(string Id, double Distance) : Feature(Id);

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

/// <summary>The stored model: the only thing that is versioned. Geometry is always regenerated from it.</summary>
public sealed record PartDocument(IReadOnlyList<Feature> Features)
{
    public const string FormatVersion = "isoform/0";
    public const string Units = "mm";

    public Feature this[string id] => Features.Single(f => f.Id == id);
}
