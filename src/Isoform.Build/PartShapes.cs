using BasAutomation.Geometry;
using Isoform.Document;

namespace Isoform.Build;

public enum OutlineEdgeKind { Line, Arc, Circle }

/// <summary>
/// A line, arc or circle of a plate's outline in the plate's own XY. An arc runs counter-clockwise round
/// <see cref="Centre"/> from <see cref="From"/> to <see cref="To"/> through <see cref="Sweep"/> radians; a circle's
/// ends are its centre.
/// </summary>
public readonly record struct OutlineEdge(OutlineEdgeKind Kind, Point2D From, Point2D To, Point2D Centre, double Radius, double Sweep)
{
    public double Length => Kind == OutlineEdgeKind.Line ? (To - From).Length : Radius * Sweep;

    /// <summary>The point halfway along it (a circle's centre).</summary>
    public Point2D Middle => Kind switch
    {
        OutlineEdgeKind.Line => new((From.X + To.X) / 2, (From.Y + To.Y) / 2),
        OutlineEdgeKind.Arc => At(Math.Atan2(From.Y - Centre.Y, From.X - Centre.X) + Sweep / 2),
        _ => Centre,
    };

    Point2D At(double angle) => new(Centre.X + Radius * Math.Cos(angle), Centre.Y + Radius * Math.Sin(angle));

    /// <summary>The edge's extreme points along ±x and ±y, with its ends: what a box round it needs.</summary>
    public IEnumerable<Point2D> Extremes()
    {
        if (Kind != OutlineEdgeKind.Circle) yield return From;
        if (Kind != OutlineEdgeKind.Circle) yield return To;
        if (Kind == OutlineEdgeKind.Line) yield break;
        var start = Math.Atan2(From.Y - Centre.Y, From.X - Centre.X);
        for (var k = 0; k < 4; k++)
        {
            var a = k * Math.PI / 2;
            var along = ((a - start) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
            if (along <= Sweep) yield return At(a);
        }
    }
}

/// <summary>
/// A plate as <see cref="PlateMethod"/> writes it: a sketch of lines, arcs and circles in the plate's own XY,
/// extruded 0 to <paramref name="Thickness"/>.
/// </summary>
public sealed record PlateShape(Sketch Sketch, double Thickness)
{
    public static PlateShape? Of(PartDocument document) =>
        document.Features is [Sketch s, Extrude { Start: 0, Op: BooleanOp.New } e] && e.Sketch == s.Id && e.Regions is null ? new(s, e.End) : null;

    public IReadOnlyList<OutlineEdge> Edges => [.. Sketch.Entities.Where(e => !e.Construction).Select(Edge)];

    OutlineEdge Edge(SketchEntity e)
    {
        Point2D P(string name) => new(Sketch.Points[name][0], Sketch.Points[name][1]);
        switch (e)
        {
            case SketchLine l:
                return new(OutlineEdgeKind.Line, P(l.From), P(l.To), default, 0, 0);
            case SketchArc arc:
                var (c, from, to) = (P(arc.Center), P(arc.From), P(arc.To));
                var sweep = Math.Atan2(to.Y - c.Y, to.X - c.X) - Math.Atan2(from.Y - c.Y, from.X - c.X);
                while (sweep <= 0) sweep += 2 * Math.PI;
                return new(OutlineEdgeKind.Arc, from, to, c, (from - c).Length, sweep);
            case SketchCircle circle:
                var centre = P(circle.Center);
                return new(OutlineEdgeKind.Circle, centre, centre, centre, circle.Radius, 2 * Math.PI);
            default:
                throw new NotSupportedException(e.GetType().Name);
        }
    }

    /// <summary>The plate's area, exactly: its outer loop's less its holes'.</summary>
    public double Area
    {
        get
        {
            // Each loop's area by Green's theorem (an arc as its chord plus its segment), the loop found by walking its
            // edges end to end whichever way they were written. A plate is one region: the largest loop is its outline.
            var edges = Edges;
            var areas = edges.Where(e => e.Kind == OutlineEdgeKind.Circle).Select(c => Math.PI * c.Radius * c.Radius).ToList();
            var open = edges.Where(e => e.Kind != OutlineEdgeKind.Circle).ToList();
            while (open.Count > 0)
            {
                var (start, at, area) = (open[0].From, open[0].To, Signed(open[0], true));
                open.RemoveAt(0);
                while ((at - start).Length > 1e-6)
                {
                    var k = open.FindIndex(e => (e.From - at).Length <= 1e-6 || (e.To - at).Length <= 1e-6);
                    var forward = (open[k].From - at).Length <= 1e-6;
                    area += Signed(open[k], forward);
                    at = forward ? open[k].To : open[k].From;
                    open.RemoveAt(k);
                }
                areas.Add(Math.Abs(area));
            }
            return 2 * areas.Max() - areas.Sum();
        }
    }

    static double Signed(OutlineEdge e, bool forward)
    {
        var (a, b) = forward ? (e.From, e.To) : (e.To, e.From);
        var chord = (a.X * b.Y - b.X * a.Y) / 2;
        if (e.Kind == OutlineEdgeKind.Line) return chord;
        var segment = e.Radius * e.Radius / 2 * (e.Sweep - Math.Sin(e.Sweep));
        return chord + (forward ? segment : -segment);
    }

    /// <summary>The box round the outline in the plate's own XY.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Box
    {
        get
        {
            var points = Edges.SelectMany(e => e.Extremes()).ToList();
            return (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        }
    }
}

/// <summary>
/// A tube as <see cref="TubeMethod"/> writes it: an annulus of <paramref name="Outside"/> diameter and
/// <paramref name="Wall"/> thickness along the part's z, cut by end A's plane through the origin and end B's through
/// (0, 0, <paramref name="Length"/>); each end's outward normal in the part's frame. The wall is read back to the nanometre.
/// </summary>
public sealed record TubeShape(double Outside, double Wall, double Length, Vector3D EndA, Vector3D EndB)
{
    public static TubeShape? Of(PartDocument document) =>
        document.Features is [Sketch s, Extrude { Op: BooleanOp.New } e, PlaneCut a, PlaneCut b] && e.Sketch == s.Id
        && s.Entities.OfType<SketchCircle>().OrderByDescending(c => c.Radius).ToList() is [var outside, .. var bore] && bore.Count <= 1
            ? new(2 * outside.Radius, Math.Round(outside.Radius - (bore.Count == 1 ? bore[0].Radius : 0), 9), b.Point[2], V(a.Normal), V(b.Normal))
            : null;

    static Vector3D V(double[] v) => new Vector3D(v[0], v[1], v[2]).Normalized();

    /// <summary>Each end's angle off square, in degrees.</summary>
    public double AngleA => Off(EndA);
    public double AngleB => Off(EndB);

    static double Off(Vector3D n) => Math.Acos(Math.Min(1, Math.Abs(n.Z))) * 180 / Math.PI;

    // An end's plane as z = slope · (x, y): its tilt across the axis.
    static Vector2D Slope(Vector3D n) => new(-n.X / n.Z, -n.Y / n.Z);

    double R => Outside / 2;

    /// <summary>From the tip of one end to the tip of the other, along the axis: the length to cut from stock.</summary>
    public double LongPoint => Length + R * (Slope(EndA).Length + Slope(EndB).Length);

    /// <summary>Between the two ends' innermost points, along the axis.</summary>
    public double ShortPoint => Length - R * (Slope(EndA).Length + Slope(EndB).Length);

    /// <summary>The turn round the axis from end A's long point to end B's, in degrees: 180 for parallel cuts, 0 for a cut like a V.</summary>
    public double Turn
    {
        get
        {
            // End A's long point is where its plane reaches lowest (along −slope), B's where it reaches highest (+slope).
            var (a, b) = (Slope(EndA), Slope(EndB));
            if (a.Length < 1e-9 || b.Length < 1e-9) return 0;
            return Math.Abs(Math.Atan2(a.Cross(-b), a.Dot(-b))) * 180 / Math.PI;
        }
    }

    /// <summary>The tube's volume, exactly: its annulus times the axis between the end planes (they cut it through its centroid).</summary>
    public double Volume => Math.PI * (R * R - (R - Wall) * (R - Wall)) * Length;
}
