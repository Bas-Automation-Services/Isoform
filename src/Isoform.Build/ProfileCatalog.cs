using BasAutomation.Geometry;
using Isoform.Document;

namespace Isoform.Build;

/// <summary>
/// Profiles by id (<c>IPE200</c>, <c>RHS100x50x4</c>, <c>CHS48.3x2.9</c>) for the beam sections of a build: the beam
/// method resolves a section's profile here. Isoform has no catalog of its own; a beam method brings one.
/// </summary>
public interface IProfileCatalog
{
    /// <summary>The profile <paramref name="id"/>, or null when the catalog has none by that id.</summary>
    /// <exception cref="NotSupportedException">The catalog has it but not as lines and arcs (an elliptical hollow section).</exception>
    CatalogProfile? Find(string id);
}

/// <summary>
/// A profile as exact lines, arcs and circles: its outer loop and its holes (a hollow section's bore), in the section's
/// own XY. The section's frame is the beam's: z along the axis, y the beam's up direction, x = y × z, and the origin on
/// the beam's line where the catalog puts it (the centre of an I or a box section).
/// </summary>
public sealed record CatalogProfile(string Id, string Name, IReadOnlyList<OutlineEdge> Outer, IReadOnlyList<IReadOnlyList<OutlineEdge>> Holes)
{
    /// <summary>The catalog's material for it, if it names one.</summary>
    public string? Material { get; init; }

    /// <summary>A round section (a tube or a round bar): one circle outside.</summary>
    public bool Round => Outer is [{ Kind: OutlineEdgeKind.Circle }];

    /// <summary>The section's area, exactly: the outer loop's less the holes'.</summary>
    public double Area => new PlateShape(Sketch("section"), 0).Area;

    /// <summary>The box round the section in its own XY.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Box => new PlateShape(Sketch("section"), 0).Box;

    /// <summary>
    /// The section as a sketch on the XY plane: the outer loop's entities <c>outer-1</c>… (a circle <c>outer</c>), a
    /// hole's <c>bore-1</c>… (<c>bore</c>; <c>bore-2-1</c>… when there are several); without <paramref name="bored"/>
    /// only the outer loop, the section's solid envelope.
    /// </summary>
    public Sketch Sketch(string id, bool bored = true)
    {
        var points = new Dictionary<string, double[]>();
        var entities = new List<SketchEntity>();
        // Neighbouring edges share their ends: one point each, whatever rounding the catalog's arithmetic left.
        string Point(Point2D p)
        {
            foreach (var (name, at) in points)
                if (Math.Abs(at[0] - p.X) <= 1e-9 && Math.Abs(at[1] - p.Y) <= 1e-9) return name;
            var key = $"p{points.Count + 1}";
            points[key] = [p.X, p.Y];
            return key;
        }
        void Loop(IReadOnlyList<OutlineEdge> loop, string prefix)
        {
            for (var i = 0; i < loop.Count; i++)
            {
                var (e, name) = (loop[i], loop.Count == 1 ? prefix : $"{prefix}-{i + 1}");
                entities.Add(e.Kind switch
                {
                    OutlineEdgeKind.Line => new SketchLine(name, Point(e.From), Point(e.To)),
                    OutlineEdgeKind.Arc => new SketchArc(name, Point(e.Centre), Point(e.From), Point(e.To)),
                    _ => new SketchCircle(name, Point(e.Centre), e.Radius),
                });
            }
        }
        Loop(Outer, "outer");
        if (bored)
            for (var k = 0; k < Holes.Count; k++) Loop(Holes[k], Holes.Count == 1 ? "bore" : $"bore-{k + 1}");
        return new Sketch(id, new SketchPlacement(new SketchPlane([0, 0, 0], [1, 0, 0], [0, 0, 1])), points, entities, []);
    }
}
