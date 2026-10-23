using Isoform.Document;

namespace Isoform.Sketching;

/// <summary>
/// A part's edge as a sketch sees it, in sketch coordinates: the line through (X, Y) along the unit (Dx, Dy), or the
/// circle round (X, Y) of <see cref="CarrierCircle.Radius"/>. A projected entity's points are held on it.
/// </summary>
public abstract record SketchCarrier;

public sealed record CarrierLine(double X, double Y, double Dx, double Dy) : SketchCarrier;

public sealed record CarrierCircle(double X, double Y, double Radius) : SketchCarrier;

/// <summary>
/// One piece of a projected edge: a point at <paramref name="From"/> (no carrier: an edge seen end-on), a line on its
/// carrier from <paramref name="From"/> to <paramref name="To"/>, an arc of its circle counter-clockwise from
/// <paramref name="From"/> to <paramref name="To"/>, or the whole circle (no ends).
/// </summary>
public sealed record ProjectedPiece(SketchCarrier? Carrier, double[]? From = null, double[]? To = null);

public static partial class SketchEdits
{
    /// <summary>Two points closer than this, in mm, are one: a projected end on a point already there takes that point.</summary>
    public const double Same = 1e-9;

    /// <summary>
    /// Adds the pieces of a projected edge as ordinary entities: a line, an arc or a circle each, named
    /// <c>projected</c>, <c>projected-2</c>, …, with points <c>&lt;entity&gt;-from</c>, <c>-to</c> and <c>-centre</c>, and a
    /// point <c>projected</c> for an edge seen end-on. An end or centre on a point already there takes that point, so
    /// edges projected one after another close into loops. With <paramref name="link"/> (the edge's two faces) each
    /// entity is construction geometry that keeps the reference: regeneration holds its points on the projection.
    /// </summary>
    public static SketchEdit Project(Sketch sketch, IReadOnlyList<ProjectedPiece> pieces, string[]? link = null)
    {
        if (pieces.Count == 0) throw new ArgumentException("there is nothing to project");
        if (link is not null && pieces.Any(p => p.Carrier is null))
            throw new ArgumentException($"the edge between {link[0]} and {link[1]} is seen end-on and projects to a point, which cannot be linked; project a copy");

        var taken = new HashSet<string>(sketch.Points.Keys.Concat(sketch.Entities.Select(e => e.Id)), StringComparer.Ordinal);
        string Fresh(string name)
        {
            var n = name;
            for (var k = 2; !taken.Add(n); k++) n = FormattableString.Invariant($"{name}-{k}");
            return n;
        }
        var points = sketch.Points.ToDictionary(p => p.Key, p => p.Value);
        var entities = sketch.Entities.ToList();
        var added = new List<string>();
        string PointAt(double[] at, string name)
        {
            if (points.FirstOrDefault(p => Math.Abs(p.Value[0] - at[0]) <= Same && Math.Abs(p.Value[1] - at[1]) <= Same).Key is { } there) return there;
            name = Fresh(name);
            points[name] = [at[0], at[1]];
            added.Add(name);
            return name;
        }

        foreach (var piece in pieces)
        {
            if (piece.Carrier is null)
            {
                PointAt(piece.From!, "projected");
                continue;
            }
            var id = Fresh("projected");
            added.Add(id);
            SketchEntity entity = piece.Carrier switch
            {
                CarrierLine => new SketchLine(id, PointAt(piece.From!, $"{id}-from"), PointAt(piece.To!, $"{id}-to")),
                CarrierCircle c when piece.From is null => new SketchCircle(id, PointAt([c.X, c.Y], $"{id}-centre"), c.Radius),
                CarrierCircle c => new SketchArc(id, PointAt([c.X, c.Y], $"{id}-centre"), PointAt(piece.From!, $"{id}-from"), PointAt(piece.To!, $"{id}-to")),
                _ => throw new ArgumentException(piece.Carrier.ToString()),
            };
            entities.Add(link is null ? entity : entity with { Project = link, Construction = true });
        }
        var edited = sketch with { Points = points, Entities = entities };
        return new SketchEdit(edited, added, []);
    }
}
