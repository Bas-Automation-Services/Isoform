using BasAutomation.Geometry;
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
        var kept = part.Bodies.Where(b => !left.Contains(b.Name)).ToList();
        var bodies = kept.Select(b => b.Field).ToList();
        (Document, Result) = (document, result);
        Field = (part.Bodies.Count == 0 ? part.Field : bodies.Count == 1 ? bodies[0] : bodies.Count > 1 ? new MinField(bodies) : null)
            ?? throw new ArgumentException("the design has no solid to build", nameof(result));
        Bounds = part.Bodies.Count == 0 ? new Box3D(part.Min, part.Max)
            : new Box3D(new Point3D(kept.Min(b => b.Min.X), kept.Min(b => b.Min.Y), kept.Min(b => b.Min.Z)), new Point3D(kept.Max(b => b.Max.X), kept.Max(b => b.Max.Y), kept.Max(b => b.Max.Z)));
    }

    public PartDocument Document { get; }
    public FrepResult Result { get; }

    /// <summary>The design's solid: the union of its bodies, keep-outs left out.</summary>
    public Field Field { get; }

    /// <summary>A box holding <see cref="Field"/>'s solid.</summary>
    public Box3D Bounds { get; }

    /// <summary>
    /// The copies a name binds to, each with its faces: the face itself (<c>leg/a</c>) first, then its pattern copies
    /// (<c>legs/1:leg/a</c>…) by name; a copy's own name binds that copy alone.
    /// </summary>
    public IReadOnlyList<(string Name, IReadOnlyList<TopoFace> Faces)> Copies(string name) =>
        [.. Faces(name).GroupBy(f => f.Sources.First(s => s == name || s.EndsWith(":" + name, StringComparison.Ordinal)))
            .OrderBy(g => g.Key == name ? "" : g.Key, StringComparer.Ordinal).Select(g => (g.Key, (IReadOnlyList<TopoFace>)[.. g]))];

    /// <summary>The middles of the mesh polygons of <paramref name="faces"/>: points on them.</summary>
    public IEnumerable<Point3D> PointsOn(IReadOnlyList<TopoFace> faces)
    {
        var (mesh, set) = (Result.Mesh!, faces.Select(f => f.Index).ToHashSet());
        for (var q = 0; q < mesh.Quads.Count; q++)
        {
            if (!set.Contains(Result.FaceOfQuad[q])) continue;
            var (a, b, c, d) = (mesh.Vertices[mesh.Quads[q].A], mesh.Vertices[mesh.Quads[q].B], mesh.Vertices[mesh.Quads[q].C], mesh.Vertices[mesh.Quads[q].D]);
            yield return new Point3D((a.X + b.X + c.X + d.X) / 4, (a.Y + b.Y + c.Y + d.Y) / 4, (a.Z + b.Z + c.Z + d.Z) / 4);
        }
    }

    /// <summary>
    /// The faces a build document's name binds to: the face itself and its pattern copies (<c>leg/a</c> covers
    /// <c>legs/1:leg/a</c>), or the one copy its own name picks.
    /// </summary>
    public IReadOnlyList<TopoFace> Faces(string name) =>
        Result.Topology?.Faces.Where(f => f.Sources.Any(s => s == name || s.EndsWith(":" + name, StringComparison.Ordinal))).ToList() ?? [];
}
