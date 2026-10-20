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

/// <summary>The stored model: the only thing that is versioned. Geometry is always regenerated from it.</summary>
public sealed record PartDocument(IReadOnlyList<Feature> Features)
{
    public const string FormatVersion = "isoform/0";
    public const string Units = "mm";

    public Feature this[string id] => Features.Single(f => f.Id == id);
}
