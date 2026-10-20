namespace Isoform.Document;

/// <summary>How a feature's solid combines with the part so far.</summary>
public enum BooleanOp { New, Join, Cut }

/// <summary>
/// A node in the feature graph. Ids are assigned at creation and never derived from list position,
/// so diffs and merges stay stable when features are inserted or reordered.
/// </summary>
public abstract record Feature(string Id);

/// <summary>Axis-aligned block from <paramref name="Origin"/> with <paramref name="Size"/> (W, L, H) in mm.</summary>
public sealed record Block(string Id, BooleanOp Op, double[] Origin, double[] Size) : Feature(Id);

/// <summary>Through-all hole along Z at <paramref name="Center"/> (x, y).</summary>
public sealed record Hole(string Id, double[] Center, double Diameter) : Feature(Id);

/// <summary>Removes all material on the side <paramref name="Normal"/> points to, starting at <paramref name="Point"/>.</summary>
public sealed record PlaneCut(string Id, double[] Point, double[] Normal) : Feature(Id);

/// <summary>Constant-radius fillet on the edge between two source faces, e.g. ["plate/z+", "plate/x+"].</summary>
public sealed record Fillet(string Id, string[] Edge, double Radius) : Feature(Id);

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
public sealed record Extrude(string Id, BooleanOp Op, SketchPlane Plane, Profile Profile, double Start, double End) : Feature(Id);

/// <summary>
/// A profile revolved round the sketch's y axis: profile x is the distance from the axis (never negative), y the
/// height along it. <paramref name="Angle"/> in degrees, right-handed round the axis from the sketch plane; below
/// 360 the two flat ends are <c>id/start</c> and <c>id/end</c>.
/// </summary>
public sealed record Revolve(string Id, BooleanOp Op, SketchPlane Plane, Profile Profile, double Angle = 360) : Feature(Id);

/// <summary>The stored model: the only thing that is versioned. Geometry is always regenerated from it.</summary>
public sealed record PartDocument(IReadOnlyList<Feature> Features)
{
    public const string FormatVersion = "isoform/0";
    public const string Units = "mm";

    public Feature this[string id] => Features.Single(f => f.Id == id);
}
