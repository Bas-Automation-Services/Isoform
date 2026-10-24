using Isoform.Document;
using Isoform.Geometry;
using Isoform.Modeling;

namespace Isoform.Build;

/// <summary>
/// The design a build runs on: its document, its regeneration, and its solid without the keep-out bodies.
/// </summary>
public sealed class Design
{
    public Design(PartDocument document, FrepResult result, IEnumerable<string> keepouts)
    {
        var (part, left) = (result.Part, keepouts.ToHashSet());
        var bodies = part.Bodies.Where(b => !left.Contains(b.Name)).Select(b => b.Field).ToList();
        (Document, Result) = (document, result);
        Field = (part.Bodies.Count == 0 ? part.Field : bodies.Count == 1 ? bodies[0] : bodies.Count > 1 ? new MinField(bodies) : null)
            ?? throw new ArgumentException("the design has no solid to build", nameof(result));
    }

    public PartDocument Document { get; }
    public FrepResult Result { get; }

    /// <summary>The design's solid: the union of its bodies, keep-outs left out.</summary>
    public Field Field { get; }

    /// <summary>
    /// The faces a build document's name binds to: the face itself and its pattern copies (<c>leg/a</c> covers
    /// <c>legs/1:leg/a</c>), or the one copy its own name picks.
    /// </summary>
    public IReadOnlyList<TopoFace> Faces(string name) =>
        Result.Topology?.Faces.Where(f => f.Sources.Any(s => s == name || s.EndsWith(":" + name, StringComparison.Ordinal))).ToList() ?? [];
}
